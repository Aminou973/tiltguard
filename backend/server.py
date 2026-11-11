"""Tiltguard - tilt lockout for TradingView (Tradovate) and NinjaTrader 8.

Can run standalone or behind a reverse proxy next to a trade journal (see bridge.py).
Devices (TradingView extension, NT8 add-on) authenticate with the X-TG-Key header. The web UI
shares the journal's login: with JOURNAL_PASSWORD set, the journal's session cookie is accepted
here and signing in here also signs you into the journal.

Run:   uvicorn server:app --host 127.0.0.1 --port 8940
Local password (only without JOURNAL_PASSWORD):  python server.py --set-password"""
import hashlib
import hmac
import io
import json
import logging
import os
import re
import secrets
import sys
import time
import zipfile
from contextlib import asynccontextmanager

from fastapi import Body, Depends, FastAPI, HTTPException, Query, Request, Response
from fastapi.responses import FileResponse, HTMLResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles

import bars
import bridge
import db
import guard
import rules as R

VERSION = "0.3.0"
HERE = os.path.dirname(os.path.abspath(__file__))
WEB = os.path.join(HERE, "web")
ROOT = os.path.dirname(HERE)
SESSION_DAYS = 30
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")



@asynccontextmanager
async def lifespan(_app):
    api_key()
    bridge.start()
    yield


app = FastAPI(title="Tiltguard", version=VERSION, docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)


# ---------------------------------------------------------------- auth
def _scrypt(pw, salt):
    return hashlib.scrypt(pw.encode(), salt=salt, n=2 ** 14, r=8, p=1, dklen=32).hex()


def set_password(pw):
    salt = secrets.token_bytes(16)
    db.kv_set("auth", {"salt": salt.hex(), "hash": _scrypt(pw, salt)})


def check_password(pw):
    jp = os.environ.get("JOURNAL_PASSWORD")
    if jp:
        return hmac.compare_digest(pw.encode(), jp.encode())
    auth = db.kv_get("auth")
    return bool(auth) and hmac.compare_digest(_scrypt(pw, bytes.fromhex(auth["salt"])), auth["hash"])


def api_key():
    k = db.kv_get("api_key")
    if not k:
        k = "tg_" + secrets.token_urlsafe(24)
        db.kv_set("api_key", k)
    return k


def _session_ok(request):
    if bridge.enabled():
        tok = request.cookies.get("journal_session", "")
        if tok and hmac.compare_digest(tok, bridge.session_cookie()):
            return True
    tok = request.cookies.get("tg_s")
    if not tok:
        return False
    r = db.q1("SELECT expires FROM sessions WHERE token=?", (hashlib.sha256(tok.encode()).hexdigest(),))
    return bool(r and r["expires"] > time.time())


def web_user(request: Request):
    if not _session_ok(request):
        raise HTTPException(401, "login required")
    return True


def device(request: Request):
    k = request.headers.get("x-tg-key", "")
    if k and hmac.compare_digest(k, api_key()):
        return True
    if _session_ok(request):
        return True
    raise HTTPException(401, "bad key")


def _https(request):
    return request.url.scheme == "https" or request.headers.get("x-forwarded-proto") == "https"


_login_fail = {}


@app.post("/api/login")
def login(request: Request, response: Response, body: dict = Body(...)):
    ip = request.headers.get("x-forwarded-for", request.client.host if request.client else "?").split(",")[0]
    fails = [t for t in _login_fail.get(ip, []) if time.time() - t < 900]
    if len(fails) >= 8:
        raise HTTPException(429, "too many attempts, wait 15 min")
    if not check_password(str(body.get("password") or "")):
        _login_fail[ip] = fails + [time.time()]
        time.sleep(0.5)
        raise HTTPException(401, "wrong password")
    tok = secrets.token_urlsafe(32)
    with db.tx() as c:
        c.execute("DELETE FROM sessions WHERE expires<?", (time.time(),))
        c.execute("INSERT INTO sessions(token,created,expires) VALUES(?,?,?)",
                  (hashlib.sha256(tok.encode()).hexdigest(), time.time(), time.time() + SESSION_DAYS * 86400))
    response.set_cookie("tg_s", tok, max_age=SESSION_DAYS * 86400, httponly=True, samesite="lax",
                        secure=_https(request), path="/")
    if bridge.enabled():  # one password for both apps
        response.set_cookie("journal_session", bridge.session_cookie(), max_age=SESSION_DAYS * 86400,
                            httponly=True, samesite="lax", secure=_https(request), path="/")
    return {"ok": True}


@app.post("/api/logout")
def logout(request: Request, response: Response):
    tok = request.cookies.get("tg_s")
    if tok:
        with db.tx() as c:
            c.execute("DELETE FROM sessions WHERE token=?", (hashlib.sha256(tok.encode()).hexdigest(),))
    response.delete_cookie("tg_s", path="/")
    response.delete_cookie("journal_session", path="/")
    return {"ok": True}


@app.get("/api/me")
def me(_=Depends(web_user)):
    return {"ok": True, "version": VERSION, "settings": guard.settings(), "day": guard.today(),
            "journal": bridge.enabled(), "bridge": bridge.state}


@app.post("/api/password")
def change_password(body: dict = Body(...), _=Depends(web_user)):
    if bridge.enabled():
        raise HTTPException(400, "The password is the journal's JOURNAL_PASSWORD (set on the server).")
    pw = str(body.get("password") or "")
    if len(pw) < 8:
        raise HTTPException(400, "8 characters minimum")
    set_password(pw)
    return {"ok": True}


@app.get("/api/device")
def device_info(request: Request, _=Depends(web_user)):
    host = request.headers.get("x-forwarded-host") or request.headers.get("host") or "127.0.0.1:8940"
    prefix = request.headers.get("x-forwarded-prefix", "")
    scheme = "https" if _https(request) else "http"
    return {"key": api_key(), "server": f"{scheme}://{host}{prefix}".rstrip("/")}


@app.post("/api/device/rotate")
def rotate_key(_=Depends(web_user)):
    k = "tg_" + secrets.token_urlsafe(24)
    db.kv_set("api_key", k)
    return {"key": k}


def _local_only(request: Request):
    """The bars feed is for the journal on this host: refuse anything that came through the proxy."""
    host = request.client.host if request.client else ""
    if host not in ("127.0.0.1", "::1", "testclient") or request.headers.get("x-forwarded-for"):
        raise HTTPException(403, "local only")
    return True


@app.get("/api/bars")
def get_bars(symbol: str, res: str = "1m", start: int = Query(0, alias="from"), end: int = Query(0, alias="to"),
             _=Depends(_local_only)):
    try:
        return bars.history(symbol, res, start, end or int(time.time() * 1000))
    except (ValueError, FileNotFoundError) as e:
        raise HTTPException(400, str(e))


@app.get("/api/bars/health")
def bars_health(_=Depends(_local_only)):
    return bars.health()


@app.get("/health")
def health():
    return {"ok": True, "version": VERSION, "bridge": {k: bridge.state[k] for k in ("last_ok", "last_error")}}


# ---------------------------------------------------------------- device endpoints
@app.post("/api/ingest/state")
def ingest_state(payload: dict = Body(...), _=Depends(device)):
    return guard.ingest(payload)


@app.post("/api/ingest/fills")
def ingest_fills(payload: dict = Body(...), _=Depends(device)):
    fills = payload.get("fills") or []
    n = guard.import_fills(fills, str(payload.get("platform") or "import"))
    return {"ok": True, "received": len(fills), "accounts_rebuilt": n}


@app.get("/api/guard/status")
def guard_status(_=Depends(device)):
    return guard.status_all()


@app.post("/api/guard/manual-lock")
def manual_lock(body: dict = Body(default={}), _=Depends(device)):
    minutes = body.get("minutes")
    if minutes not in (None, ""):
        minutes = float(minutes)
        if not 5 <= minutes <= 7 * 24 * 60:
            raise HTTPException(400, "minutes must be between 5 and 10080")
    account = str(body.get("account") or "").strip() or None
    return guard.manual_lock(body.get("reason") or "Done for today", minutes or None, bool(body.get("flatten", True)), account=account)


# ---------------------------------------------------------------- guard (web)
@app.get("/api/guard/live")
def guard_live(_=Depends(web_user)):
    st = guard.status_all()
    d = st["day"]
    acc = {a["name"]: a for a in db.q("SELECT * FROM accounts")}
    for name, v in st["accounts"].items():
        a = acc.get(name, {})
        v.update(label=a.get("label") or "", firm=a.get("firm") or "", grp=a.get("grp") or "")
        ls = db.q1("SELECT positions, balance FROM live_state WHERE account=?", (name,))
        v["positions"] = json.loads(ls["positions"]) if ls and ls["positions"] else []
        v["balance"] = ls["balance"] if ls else None
    st["locks_today"] = db.q("SELECT * FROM locks WHERE day=? OR until>? ORDER BY started DESC", (d, time.time()))
    st["events_today"] = db.q("SELECT * FROM events WHERE day=? ORDER BY ts DESC LIMIT 200", (d,))
    st["rules"] = guard.effective_rules()
    st["pending"] = db.kv_get("rules_pending", {})
    st["bridge"] = {**bridge.state, "enabled": bridge.enabled(),
                    "queued": db.q1("SELECT COUNT(*) n FROM fills f JOIN accounts a ON a.name=f.account"
                                    " WHERE f.pushed=0 AND a.journal=1")["n"]}
    return st


@app.get("/api/history")
def history(request: Request, _=Depends(web_user)):
    p = request.query_params
    days = min(int(p.get("days", 30)), 365)
    since = time.time() - days * 86400
    return {"locks": db.q("SELECT * FROM locks WHERE started>? ORDER BY started DESC", (since,)),
            "events": db.q("SELECT * FROM events WHERE ts>? AND type IN ('lock','cooldown','flatten','blocked_order',"
                           "'rules_change','rules_pending','rules_applied') ORDER BY ts DESC LIMIT 500", (since,))}


@app.get("/api/rules")
def get_rules(_=Depends(web_user)):
    guard.apply_due_pending()
    store = db.kv_get("rules", {"default": dict(R.DEFAULT_RULES), "accounts": {}})
    ok, why = guard.loosen_allowed()
    accts = db.q("SELECT name, label, firm, platform FROM accounts WHERE hidden=0 ORDER BY journal DESC, last_seen DESC")
    loosen = {"default": {"ok": ok, "why": why}}
    effective = {}
    for a in accts:
        aok, awhy = guard.loosen_allowed(account=a["name"])
        loosen[a["name"]] = {"ok": aok, "why": awhy}
        effective[a["name"]] = guard.effective_rules(a["name"])
    return {"definitions": [{"key": k, "label": l, "kind": kind, "unit": u, "help": h} for k, l, kind, u, h in R.RULES],
            "default": guard.effective_rules(), "accounts": store.get("accounts", {}), "effective": effective,
            "account_list": accts, "pending": db.kv_get("rules_pending", {}), "loosen": loosen,
            "loosen_now": ok, "loosen_why": why}


@app.put("/api/rules")
def put_rules(body: dict = Body(...), _=Depends(web_user)):
    scope = str(body.get("scope") or "default")
    try:
        return guard.update_rules(scope, body.get("rules") or {})
    except ValueError as e:
        raise HTTPException(400, str(e))


SCOPE_ORDER = {"all": 0, "group": 1, "account": 2}


@app.get("/api/settings")
def get_settings(_=Depends(web_user)):
    return guard.settings()


LIST_KEYS = ("block_sites", "block_apps", "distraction_sites", "distraction_apps")
HM = re.compile(r"^([01]\d|2[0-3]):[0-5]\d$")


def _clean_site(x):
    x = re.sub(r"^https?://", "", str(x).strip().lower()).split("/")[0]
    return x[4:] if x.startswith("www.") else x


def _looser_desktop(cur, new):
    """Anything that weakens the lockout (fewer targets, later / shorter / disabled schedule)."""
    for k in LIST_KEYS:
        if set(cur.get(k) or []) - set(new.get(k) or []):
            return True
    if cur["block_distractions"] and not new["block_distractions"]:
        return True
    if cur["auto_lock_time"] and (not new["auto_lock_time"] or new["auto_lock_time"] > cur["auto_lock_time"]):
        return True
    if cur["weekend_lock"] and (not new["weekend_lock"] or new["weekend_lock_from"] > cur["weekend_lock_from"]):
        return True
    if cur["lock_until"] and new["lock_until"] != cur["lock_until"] or new.get("lock_until_tz") != cur.get("lock_until_tz"):
        ref = time.time()
        if guard.lock_end(ref, new) < guard.lock_end(ref, cur):      # locks would end sooner
            return True
    return False


DESKTOP_KEYS = LIST_KEYS + ("block_distractions", "lock_until", "lock_until_tz", "auto_lock_time", "weekend_lock",
                            "weekend_lock_from")


@app.put("/api/settings")
def put_settings(body: dict = Body(...), _=Depends(web_user)):
    cur = guard.settings()
    new = dict(cur)
    for k in guard.DEFAULT_SETTINGS:
        if k in body:
            new[k] = body[k]
    if new["lock_scope"] not in SCOPE_ORDER:
        raise HTTPException(400, "lock_scope must be all, group or account")
    for k in ("reset_time", "weekend_lock_from"):
        if not HM.match(str(new[k])):
            raise HTTPException(400, f"{k} must be HH:MM")
    for k in ("lock_until", "auto_lock_time"):
        new[k] = str(new.get(k) or "").strip()
        if new[k] and not HM.match(new[k]):
            raise HTTPException(400, f"{k} must be HH:MM or empty")
    try:
        from zoneinfo import ZoneInfo
        ZoneInfo(str(new.get("lock_until_tz") or "America/New_York"))
    except Exception:
        raise HTTPException(400, "lock_until_tz must be an IANA time zone, e.g. Europe/Paris")
    for k in ("block_sites", "distraction_sites"):
        new[k] = list(dict.fromkeys(_clean_site(x) for x in new.get(k) or [] if str(x).strip()))
    for k in ("block_apps", "distraction_apps"):
        apps = [str(x).strip() for x in new.get(k) or [] if str(x).strip()]
        new[k] = list(dict.fromkeys(a if a.lower().endswith(".exe") else a + ".exe" for a in apps))
    new["block_distractions"] = bool(new["block_distractions"])
    new["weekend_lock"] = bool(new["weekend_lock"])
    new["commissions"] = {str(k).upper(): float(v) for k, v in (new.get("commissions") or {}).items() if v not in ("", None)}
    looser = SCOPE_ORDER[new["lock_scope"]] > SCOPE_ORDER[cur["lock_scope"]] or new["reset_time"] != cur["reset_time"]         or new["tz"] != cur["tz"] or _looser_desktop(cur, new)
    ok, why = guard.loosen_allowed()
    if looser and not ok:
        raise HTTPException(409, why.replace("looser rules start next session", "a weaker lockout setting can only be "
                                             "saved before your first trade of the day, with no lock active"))
    db.kv_set("settings", {k: new[k] for k in guard.DEFAULT_SETTINGS})
    if new["commissions"] != cur["commissions"]:
        guard.rebuild_trades([r["account"] for r in db.q("SELECT DISTINCT account FROM fills")])
    return guard.settings()


# ---------------------------------------------------------------- accounts
@app.get("/api/accounts")
def accounts(_=Depends(web_user)):
    rows = db.q("SELECT a.*, (SELECT COUNT(*) FROM trades t WHERE t.account=a.name AND t.open=0) n,"
                " (SELECT ROUND(SUM(net),2) FROM trades t WHERE t.account=a.name AND t.open=0) net,"
                " (SELECT MAX(day) FROM trades t WHERE t.account=a.name) last_day,"
                " (SELECT COUNT(*) FROM fills f WHERE f.account=a.name AND f.pushed=0) unsent"
                " FROM accounts a ORDER BY last_day DESC, a.last_seen DESC")
    store = db.kv_get("rules", {}).get("accounts", {})
    for r in rows:
        r["custom_rules"] = store.get(r["name"], {})
    return rows


@app.patch("/api/accounts/{name}")
def patch_account(name: str, body: dict = Body(...), _=Depends(web_user)):
    sets = {k: body[k] for k in ("label", "firm", "grp", "hidden", "journal") if k in body}
    if not sets:
        return {"ok": True}
    for k in ("hidden", "journal"):
        if k in sets:
            sets[k] = 1 if sets[k] else 0
    with db.tx() as c:
        c.execute(f"UPDATE accounts SET {', '.join(k + '=?' for k in sets)} WHERE name=?", (*sets.values(), name))
    return {"ok": True}


# ---------------------------------------------------------------- downloads + web
def _zip_dir(path, prefix):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for base, _, files in os.walk(path):
            for fn in files:
                if fn.endswith((".py", ".md")) or fn.startswith("."):
                    continue
                full = os.path.join(base, fn)
                z.write(full, os.path.join(prefix, os.path.relpath(full, path)))
    buf.seek(0)
    return buf


@app.get("/downloads/tiltguard-extension.zip")
def dl_extension(_=Depends(web_user)):
    return StreamingResponse(_zip_dir(os.path.join(ROOT, "extension"), "tiltguard-extension"), media_type="application/zip",
                             headers={"Content-Disposition": "attachment; filename=tiltguard-extension.zip"})


@app.get("/downloads/TiltguardAddOn.cs")
def dl_addon(_=Depends(web_user)):
    return FileResponse(os.path.join(ROOT, "nt8", "TiltguardAddOn.cs"), filename="TiltguardAddOn.cs",
                        media_type="text/plain")


@app.get("/downloads/TiltGuard.exe")
def dl_desktop(_=Depends(web_user)):
    return FileResponse(os.path.join(ROOT, "downloads", "TiltGuard.exe"), filename="TiltGuard.exe",
                        media_type="application/octet-stream")


@app.get("/downloads/import_nt8.py")
def dl_import(_=Depends(web_user)):
    return FileResponse(os.path.join(ROOT, "tools", "import_nt8.py"), filename="import_nt8.py", media_type="text/plain")


@app.get("/", response_class=HTMLResponse)
def index():
    with open(os.path.join(WEB, "index.html"), encoding="utf-8") as fh:
        return HTMLResponse(fh.read(), headers={"Cache-Control": "no-cache"})


app.mount("/static", StaticFiles(directory=WEB), name="static")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--set-password":
        import getpass
        pw = sys.argv[2] if len(sys.argv) > 2 else getpass.getpass("New password: ")
        set_password(pw)
        print("password set; device key:", api_key())
    elif len(sys.argv) > 1 and sys.argv[1] == "--key":
        print(api_key())
    else:
        import uvicorn
        uvicorn.run(app, host="127.0.0.1", port=int(os.environ.get("TG_PORT", 8940)))
