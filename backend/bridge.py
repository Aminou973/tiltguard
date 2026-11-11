"""Bridge: push every fill Tiltguard receives into the trade journal running on the same
host. Uses the journal's own import endpoint with a column mapping, so a
re-sent fill is de-duplicated by the journal (content hash per account).

Env: JOURNAL_URL (default http://127.0.0.1:8950), JOURNAL_PASSWORD (the journal's login)."""
import csv
import hashlib
import hmac
import io
import json
import logging
import os
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

import db
import guard
from instruments import POINT_VALUES

log = logging.getLogger("tiltguard.bridge")
JOURNAL_URL = os.environ.get("JOURNAL_URL", "http://127.0.0.1:8950").rstrip("/")
BATCH = 4000
MAPPING = {"symbol": "symbol", "side": "side", "quantity": "quantity", "price": "price", "fee": "fee",
           "timestamp": "timestamp"}
state = {"last_run": None, "last_ok": None, "last_error": None, "pushed": 0}


def enabled():
    return bool(os.environ.get("JOURNAL_PASSWORD"))


def session_cookie():
    pw = os.environ.get("JOURNAL_PASSWORD", "")
    return hmac.new(pw.encode(), b"session-v1", hashlib.sha256).hexdigest()


def _call(method, path, body=None, timeout=120):
    req = urllib.request.Request(JOURNAL_URL + path, method=method,
                                 data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json",
                                          "Cookie": f"journal_session={session_cookie()}"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {path}: {e.code} {e.read()[:300].decode(errors='replace')}")


def _lux_accounts():
    return {a["name"]: a["id"] for a in _call("GET", "/api/accounts?summary=1").get("accounts", [])}


def ensure_account(name, known):
    """-> journal account id for a Tiltguard account (label if set, else broker name)."""
    ids = db.kv_get("lux_accounts", {})
    if name in ids:
        return ids[name]
    a = db.q1("SELECT label, firm, platform FROM accounts WHERE name=?", (name,)) or {}
    title = name if not a.get("label") else f"{a['label']} ({name})"
    if title in known:
        lid = known[title]
    else:
        res = _call("POST", "/api/accounts", {"name": title, "kind": "import", "currency": "USD",
                                              "broker": a.get("firm") or a.get("platform") or ""})
        lid = res.get("id") or res.get("account", {}).get("id") or _lux_accounts().get(title)
        if not lid:
            raise RuntimeError(f"journal did not return an id for account {title}: {res}")
        known[title] = lid
    ids[name] = lid
    db.kv_set("lux_accounts", ids)
    return lid


def ensure_multipliers(roots):
    """Journal P&L needs the $ point value per symbol; we send root symbols (MNQ, NQ, ES...)."""
    have = _call("GET", "/api/settings").get("multipliers") or {}
    missing = {r: POINT_VALUES[r] for r in roots if r in POINT_VALUES and r not in have}
    if missing:
        _call("PATCH", "/api/settings", {"multipliers": {**have, **missing}})


def _csv(rows, fees):
    buf = io.StringIO()
    w = csv.writer(buf)
    w.writerow(["symbol", "side", "quantity", "price", "fee", "timestamp"])
    for f in rows:
        fee = f["commission"] if f["commission"] else fees.get(f["root"], 0.0) * f["qty"]
        ts = datetime.fromtimestamp(f["ts"], timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
        w.writerow([f["root"], "buy" if f["side"] > 0 else "sell", f"{f['qty']:g}", f"{f['price']:.10g}",
                    f"{fee:.2f}", ts])
    return buf.getvalue()


def push_once():
    """Send unsent fills of journal-enabled accounts. Returns number of fills acknowledged."""
    if not enabled():
        return 0
    todo = db.q("SELECT f.rowid rid, f.* FROM fills f JOIN accounts a ON a.name=f.account"
                " WHERE f.pushed=0 AND a.journal=1 ORDER BY f.account, f.ts LIMIT 50000")
    if not todo:
        return 0
    fees = {k.upper(): float(v) for k, v in (guard.settings().get("commissions") or {}).items()}
    known = _lux_accounts()
    ensure_multipliers({f["root"] for f in todo})
    by_acct, sent = {}, 0
    for f in todo:
        by_acct.setdefault(f["account"], []).append(f)
    for acct, rows in by_acct.items():
        lid = ensure_account(acct, known)
        for i in range(0, len(rows), BATCH):
            chunk = rows[i:i + BATCH]
            _call("POST", "/api/import", {"mode": "commit", "accountId": lid, "content": _csv(chunk, fees),
                                          "mapping": MAPPING, "timeZone": "UTC",
                                          "fileName": f"tiltguard-{acct}.csv"}, timeout=300)
            with db.tx() as c:
                c.executemany("UPDATE fills SET pushed=1 WHERE rowid=?", [(r["rid"],) for r in chunk])
            sent += len(chunk)
    state["pushed"] += sent
    return sent


def loop(interval=15):
    while True:
        state["last_run"] = time.time()
        try:
            n = push_once()
            state["last_ok"], state["last_error"] = time.time(), None
            if n:
                log.info("pushed %d fills to the journal", n)
        except Exception as e:  # keep the guard alive whatever the journal does
            state["last_error"] = str(e)[:500]
            log.warning("journal push failed: %s", e)
        time.sleep(interval)


def start():
    if enabled():
        threading.Thread(target=loop, name="journal-bridge", daemon=True).start()
