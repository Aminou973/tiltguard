"""The guard: ingest platform reports (TradingView extension, NT8 add-on), rebuild trades,
evaluate the rules and persist locks. Locks are never lifted early - they expire on their own."""
import json
import time

import db
import rules as R
from clock import next_reset, trading_day
from instruments import guess_firm, point_value, root_of
from trades import build_trades

DEFAULT_SETTINGS = {
    "tz": "America/New_York",
    "reset_time": "18:00",
    "lock_scope": "account",      # account (each account locks alone) | group | all
    "commissions": {},            # root -> $ per contract per side (used when the platform sends none)
    # ---- lockout targets (Chrome extension + Windows companion), only enforced while a hard lock is active
    "block_sites": ["tradingview.com", "app.hyperprop.com", "trader.tradovate.com", "topstepx.com"],
    "block_apps": ["TradingView.exe", "NinjaTrader.exe"],
    "block_distractions": True,
    "distraction_sites": ["x.com", "twitter.com", "discord.com", "youtube.com", "reddit.com"],
    "distraction_apps": ["Discord.exe"],
    # ---- schedule (times are America/New_York)
    "lock_until": "08:00",        # manual / scheduled lockouts last until this time the next morning ("" = until the reset)
    "lock_until_tz": "America/New_York",   # time zone of lock_until (e.g. Europe/Paris = your local morning)
    "auto_lock_time": "",         # daily auto-lockout on weekdays at HH:MM ("" = off)
    "weekend_lock": False,        # lock the whole weekend...
    "weekend_lock_from": "12:00",  # ...from Friday at HH:MM until Monday at lock_until
}
ONLINE_SECS = 20
BROKER_PL = ("tv", "hyperprop")     # platforms whose day P&L comes from the broker (fees included)
_flatten_log = {}


# ---------------------------------------------------------------- settings / rules
def settings():
    s = dict(DEFAULT_SETTINGS)
    s.update(db.kv_get("settings", {}))
    pend = db.kv_get("settings_pending")
    if pend and trading_day(time.time(), s["reset_time"], s["tz"]) >= pend["effective_day"]:
        s.update(pend["values"])                     # a weaker PC-lockout setting queued last session
        with db.tx() as c:
            db.kv_set("settings", {k: s[k] for k in DEFAULT_SETTINGS}, c)
            c.execute("DELETE FROM kv WHERE k='settings_pending'")
            c.execute("INSERT INTO events(ts,day,account,type,detail) VALUES(?,?,?,?,?)",
                      (time.time(), pend["effective_day"], "", "rules_applied", "Queued PC-lockout settings are now active"))
    return s


def today(now=None):
    s = settings()
    return trading_day(now or time.time(), s["reset_time"], s["tz"])


def _rules_store():
    return db.kv_get("rules", {"default": dict(R.DEFAULT_RULES), "accounts": {}})


def _pending_store():
    return db.kv_get("rules_pending", {})


def apply_due_pending(now=None):
    """Looser changes queued earlier become active once their session has started."""
    pend = _pending_store()
    if not pend:
        return
    d = today(now)
    store, changed = _rules_store(), False
    for scope in list(pend):
        p = pend[scope]
        if p["effective_day"] <= d:
            if scope == "default":
                store["default"].update(p["rules"])
            else:
                store["accounts"].setdefault(scope, {}).update(p["rules"])
            del pend[scope]
            changed = True
    if changed:
        with db.tx() as c:
            db.kv_set("rules", store, c)
            db.kv_set("rules_pending", pend, c)
            _event(c, time.time(), "", "rules_applied", "Queued rule changes are now active")


def effective_rules(account=None):
    store = _rules_store()
    r = dict(R.DEFAULT_RULES)
    r.update(store.get("default", {}))
    if account:
        r.update(store.get("accounts", {}).get(account, {}))
    return r


def loosen_allowed(now=None, account=None):
    """Looser rules apply at once only before the first trade of the day and with no active lock -
    for one account when `account` is given, else for every account (shared settings)."""
    now = now or time.time()
    d = today(now)
    who, args = ("account=? AND ", (account,)) if account else ("", ())
    if db.q1(f"SELECT 1 FROM trades WHERE {who}day=? LIMIT 1", (*args, d)):
        return False, "Already traded today - looser rules start next session."
    if db.q1(f"SELECT 1 FROM live_state WHERE {who}day=? AND pos_abs>0 LIMIT 1", (*args, d)):
        return False, "A position is open - looser rules start next session."
    if account:
        grp = (db.q1("SELECT grp FROM accounts WHERE name=?", (account,)) or {}).get("grp") or ""
        if _active_locks(account, grp, now):
            return False, "This account is locked - looser rules start next session."
    elif db.q1("SELECT 1 FROM locks WHERE until>? LIMIT 1", (now,)):
        return False, "A lock is active - looser rules start next session."
    return True, "No trades yet today - changes apply immediately."


def update_rules(scope, proposed, now=None):
    now = now or time.time()
    apply_due_pending(now)
    proposed = R.clean(proposed)
    current = effective_rules(None if scope == "default" else scope)
    ok, why = loosen_allowed(now, None if scope == "default" else scope)
    apply_now, later = R.split_changes(current, proposed, ok)
    store, pend = _rules_store(), _pending_store()
    s = settings()
    nxt = trading_day(next_reset(now, s["reset_time"], s["tz"]) + 60, s["reset_time"], s["tz"])
    with db.tx() as c:
        if apply_now:
            if scope == "default":
                store["default"].update(apply_now)
            else:
                store["accounts"].setdefault(scope, {}).update(apply_now)
            db.kv_set("rules", store, c)
            # a stricter value supersedes a queued looser one for the same key
            if scope in pend:
                for k in apply_now:
                    pend[scope]["rules"].pop(k, None)
                if not pend[scope]["rules"]:
                    del pend[scope]
            _event(c, now, "", "rules_change", json.dumps({"scope": scope, "now": apply_now}))
        if later:
            p = pend.setdefault(scope, {"effective_day": nxt, "rules": {}})
            p["effective_day"] = nxt
            p["rules"].update(later)
            _event(c, now, "", "rules_pending", json.dumps({"scope": scope, "later": later, "from": nxt}))
        db.kv_set("rules_pending", pend, c)
    return {"applied": apply_now, "pending": later, "pending_from": nxt if later else None, "why": why}


# ---------------------------------------------------------------- ingest
def _event(c, ts, account, typ, detail, day=None):
    c.execute("INSERT INTO events(ts,day,account,type,detail) VALUES(?,?,?,?,?)",
              (ts, day or today(ts), account, typ, detail))


def norm_fill(f, platform):
    side = f.get("side")
    if isinstance(side, str):
        side = 1 if side.lower() in ("buy", "b", "long", "1") else -1
    else:
        side = 1 if float(side) > 0 else -1
    ts = float(f["ts"])
    if ts > 1e12:
        ts /= 1000.0
    contract = str(f.get("contract") or f.get("instrument") or "?")
    root = f.get("root") or root_of(contract)
    qty, price = abs(float(f["qty"])), float(f["price"])
    return {
        "account": f["account"], "contract": contract, "root": root, "side": side, "qty": qty,
        "price": price, "ts": ts, "commission": abs(float(f.get("commission") or 0)),
        "point_value": point_value(root, f.get("point_value")),
        "exec_id": str(f.get("exec_id") or f.get("id") or f"{ts:.3f}|{side}|{qty:g}|{price:g}"),
        "order_id": str(f.get("order_id") or ""), "source": platform,
    }


def upsert_account(c, a, platform, now):
    name = a["name"]
    row = c.execute("SELECT name FROM accounts WHERE name=?", (name,)).fetchone()
    if row:
        c.execute("UPDATE accounts SET platform=?, broker=COALESCE(?,broker), broker_id=COALESCE(?,broker_id),"
                  " env=COALESCE(?,env), last_seen=? WHERE name=?",
                  (platform, a.get("broker"), a.get("broker_id"), a.get("env"), now, name))
    else:
        # an account connected live (TradingView/Tradovate, HyperProp, NT8) is guarded AND journaled at once;
        # only history imports (import_fills) start outside the journal
        c.execute("INSERT INTO accounts(name,platform,broker,broker_id,env,firm,journal,first_seen,last_seen)"
                  " VALUES(?,?,?,?,?,?,1,?,?)",
                  (name, platform, a.get("broker"), a.get("broker_id"), a.get("env"), guess_firm(name), now, now))


def insert_fills(c, fills, platform):
    """-> set of accounts that got new fills."""
    touched = set()
    for f in fills:
        try:
            nf = norm_fill(f, platform)
        except (KeyError, TypeError, ValueError):
            continue
        cur = c.execute(
            "INSERT OR IGNORE INTO fills(account,exec_id,contract,root,side,qty,price,ts,commission,point_value,source,order_id)"
            " VALUES(:account,:exec_id,:contract,:root,:side,:qty,:price,:ts,:commission,:point_value,:source,:order_id)", nf)
        if cur.rowcount:
            touched.add(nf["account"])
    return touched


def rebuild_trades(accounts):
    s = settings()
    comm = {k.upper(): float(v or 0) for k, v in (s.get("commissions") or {}).items()}
    fee = lambda root: comm.get(root, 0.0)
    for acct in accounts:
        fills = db.q("SELECT * FROM fills WHERE account=?", (acct,))
        trades = build_trades(fills, fee, s["reset_time"], s["tz"])
        with db.tx() as c:
            c.execute("DELETE FROM trades WHERE account=?", (acct,))
            c.executemany(
                "INSERT OR REPLACE INTO trades(id,account,day,contract,root,direction,qty,entry_ts,exit_ts,entry_price,"
                "exit_price,points,gross,fees,net,n_fills,open,source) VALUES(:id,:account,:day,:contract,:root,"
                ":direction,:qty,:entry_ts,:exit_ts,:entry_price,:exit_price,:points,:gross,:fees,:net,:n_fills,:open,:source)",
                trades)


def ingest(payload, now=None):
    now = now or time.time()
    apply_due_pending(now)
    platform = str(payload.get("platform") or "?")[:12]
    d = today(now)
    reported = []
    with db.tx() as c:
        for a in payload.get("accounts") or []:
            if not a.get("name"):
                continue
            name = str(a["name"])[:64]
            a["name"] = name
            reported.append(name)
            upsert_account(c, a, platform, now)
            positions = a.get("positions") or []
            pos_abs = sum(abs(float(p.get("qty") or 0)) for p in positions)
            prev = c.execute("SELECT day, peak_pl, trough_pl FROM live_state WHERE account=?", (name,)).fetchone()
            peak, trough = (prev["peak_pl"], prev["trough_pl"]) if prev and prev["day"] == d else (0.0, 0.0)
            c.execute(
                "INSERT INTO live_state(account,day,updated,platform,balance,total_pl,open_pl,realized_pl,positions,"
                "pos_abs,working_orders,peak_pl,trough_pl,native) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(account) DO UPDATE SET "
                "day=excluded.day, updated=excluded.updated, platform=excluded.platform, balance=excluded.balance,"
                "total_pl=excluded.total_pl, open_pl=excluded.open_pl, realized_pl=excluded.realized_pl,"
                "positions=excluded.positions, pos_abs=excluded.pos_abs, working_orders=excluded.working_orders,"
                "peak_pl=excluded.peak_pl, trough_pl=excluded.trough_pl, native=excluded.native",
                (name, d, now, platform, a.get("balance"), a.get("total_pl"), a.get("open_pl"), a.get("realized_pl"),
                 json.dumps(positions)[:4000], pos_abs, int(a.get("working_orders") or 0), peak, trough,
                 json.dumps(a["native"])[:4000] if a.get("native") else ""))
        touched = insert_fills(c, payload.get("fills") or [], platform)
        for e in payload.get("events") or []:
            _event(c, now, str(e.get("account") or "")[:64], str(e.get("type") or "client")[:32],
                   str(e.get("detail") or "")[:500])
    if touched:
        rebuild_trades(touched)
    return status_all(now, focus=reported)


def import_fills(fills, platform="import"):
    """Bulk history import. New accounts get first/last seen from their fills, not 'now',
    so old eval accounts don't show up as live."""
    with db.tx() as c:
        names = {str(f["account"]) for f in fills if f.get("account")}
        new = [n for n in names if not c.execute("SELECT 1 FROM accounts WHERE name=?", (n,)).fetchone()]
        for n in new:
            c.execute("INSERT INTO accounts(name,platform,firm,journal,first_seen,last_seen) VALUES(?,?,?,0,0,0)",
                      (n, platform, guess_firm(n)))
        touched = insert_fills(c, fills, platform)
        for n in new:
            c.execute("UPDATE accounts SET first_seen=(SELECT MIN(ts) FROM fills WHERE account=?),"
                      " last_seen=(SELECT MAX(ts) FROM fills WHERE account=?) WHERE name=?", (n, n, n))

    if touched:
        rebuild_trades(touched)
    return len(touched)


# ---------------------------------------------------------------- evaluation
def _active_locks(name, grp, now):
    return db.q("SELECT * FROM locks WHERE until>? AND (account=? OR scope='all' OR (scope='group' AND grp!='' AND grp=?))"
                " ORDER BY (kind='hard') DESC, until DESC", (now, name, grp or ""))


def evaluate_account(name, now=None, s=None):
    now = now or time.time()
    s = s or settings()
    d = trading_day(now, s["reset_time"], s["tz"])
    acct = db.q1("SELECT * FROM accounts WHERE name=?", (name,)) or {"name": name, "grp": ""}
    ls = db.q1("SELECT * FROM live_state WHERE account=?", (name,))
    fresh = bool(ls and ls["day"] == d)
    closed = db.q("SELECT net, exit_ts FROM trades WHERE account=? AND day=? AND open=0 ORDER BY exit_ts", (name, d))
    realized_fills = round(sum(t["net"] or 0 for t in closed), 2)
    open_pl = (ls["open_pl"] or 0.0) if fresh else 0.0
    if fresh and ls["platform"] in BROKER_PL and ls["total_pl"] is not None:
        day_pl = float(ls["total_pl"])        # broker's own day P&L (fees included)
    else:
        day_pl = realized_fills + open_pl
    peak = max((ls["peak_pl"] or 0.0) if fresh else 0.0, day_pl)
    trough = min((ls["trough_pl"] or 0.0) if fresh else 0.0, day_pl)
    pos_abs = (ls["pos_abs"] or 0.0) if fresh else 0.0
    ctx = {"day_pl": day_pl, "open_pl": open_pl, "peak_pl": peak, "pos_abs": pos_abs, "closed": closed}
    r = effective_rules(name)
    res = R.evaluate(r, ctx, now, s["tz"])

    with db.tx() as c:
        if fresh:
            c.execute("UPDATE live_state SET peak_pl=?, trough_pl=? WHERE account=?", (peak, trough, name))
        for b in res["hard"]:
            if not c.execute("SELECT 1 FROM locks WHERE day=? AND account=? AND rule=? AND kind='hard'",
                             (d, name, b["rule"])).fetchone():
                until = next_reset(now, s["reset_time"], s["tz"])
                c.execute("INSERT INTO locks(day,account,grp,scope,rule,kind,reason,value,limit_value,started,until,flatten)"
                          " VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                          (d, name, acct.get("grp") or "", s["lock_scope"], b["rule"], "hard", b["reason"], b["value"],
                           b["limit"], now, until, 1 if r.get("flatten_on_lock") else 0))
                _event(c, now, name, "lock", b["reason"], d)
        for b in res["cooldown"]:
            if not c.execute("SELECT 1 FROM locks WHERE account=? AND rule=? AND until=?",
                             (name, b["rule"], b["until"])).fetchone():
                c.execute("INSERT INTO locks(day,account,grp,scope,rule,kind,reason,value,limit_value,started,until,flatten)"
                          " VALUES(?,?,?,?,?,?,?,?,?,?,?,0)",
                          (d, name, acct.get("grp") or "", "account", b["rule"], "cooldown", b["reason"], b["value"],
                           b["limit"], now, b["until"]))
                _event(c, now, name, "cooldown", b["reason"], d)

    locks = _active_locks(name, acct.get("grp"), now)
    hard = next((l for l in locks if l["kind"] == "hard"), None)
    cool = next((l for l in locks if l["kind"] == "cooldown"), None)
    flatten, why = False, ""
    if pos_abs > 0 and hard and hard["flatten"]:
        flatten, why = True, hard["reason"]
    elif pos_abs > 0 and res["flatten"]:
        flatten, why = True, res["flatten"][0]["reason"]
    if flatten and now - _flatten_log.get(name, 0) > 20:
        _flatten_log[name] = now
        with db.tx() as c:
            _event(c, now, name, "flatten", why, d)
    block = bool(hard or cool or res["window"])
    reason = (hard or cool or res["window"] or {}).get("reason", "")
    lim = r.get("max_daily_loss")
    return {
        "name": name, "day": d, "locked": bool(hard), "block_entries": block, "reason": reason,
        "lock": _lock_view(hard), "cooldown": _lock_view(cool), "window": res["window"],
        "flatten": flatten, "flatten_reason": why,
        "rules": {k: r.get(k) for k in R.KEYS},
        "stats": {"day_pl": round(day_pl, 2), "open_pl": round(open_pl, 2), "realized": realized_fills,
                  "peak_pl": round(peak, 2), "trades": len(closed), "streak": ctx.get("streak", 0),
                  "pos_abs": pos_abs, "loss_left": round(lim + day_pl, 2) if lim else None,
                  "trades_left": int(r["max_trades"]) - len(closed) if r.get("max_trades") else None},
        "online": bool(ls and now - (ls["updated"] or 0) < ONLINE_SECS),
        "native": json.loads(ls["native"]) if ls and ls.get("native") else None,
        "updated": ls["updated"] if ls else None, "platform": (ls or acct).get("platform"),
    }


def _lock_view(l):
    if not l:
        return None
    return {k: l[k] for k in ("id", "rule", "kind", "reason", "started", "until", "account", "scope", "flatten")}


def status_all(now=None, focus=()):
    """Status for every account seen in the last 3 days (+ the ones just reported)."""
    now = now or time.time()
    s = settings()
    scheduled_locks(now, s)
    rows = db.q("SELECT name, label, firm FROM accounts WHERE hidden=0 AND (last_seen>? OR journal=1)"
                " ORDER BY journal DESC, last_seen DESC LIMIT 40", (now - 3 * 86400,))
    names = [r["name"] for r in rows]
    for f in focus:
        if f not in names:
            names.append(f)
    meta = {r["name"]: r for r in rows}
    st = {}
    for n in names:
        st[n] = evaluate_account(n, now, s)
        st[n]["label"] = (meta.get(n) or {}).get("label") or ""
        st[n]["firm"] = (meta.get(n) or {}).get("firm") or ""
    desk = desktop_state(now, s, st)
    return {"ok": True, "now": now, "day": today(now), "reset_at": next_reset(now, s["reset_time"], s["tz"]),
            "accounts": st, "block_sites": desk["sites"] if desk["locked"] else [], "sites": desk["sites"], "desktop": desk}


PC_LOCK_RULES = ("manual", "auto_daily", "weekend")


def desktop_state(now, s, st):
    """What the Windows companion / extension must block right now: a manual or scheduled lockout,
    or every account locked at once (one account locked alone keeps the PC usable for the others)."""
    lk = db.q1(f"SELECT * FROM locks WHERE kind='hard' AND until>? AND scope='all' AND rule IN ({','.join('?' * len(PC_LOCK_RULES))})"
               " ORDER BY until DESC LIMIT 1", (now, *PC_LOCK_RULES))
    if not lk and st and all(v["locked"] for v in st.values()):
        first = min((v["lock"] for v in st.values()), key=lambda l: l["until"])
        lk = {"until": first["until"], "reason": "All accounts locked - " + first["reason"]}
    sites = list(dict.fromkeys(s["block_sites"] + (s["distraction_sites"] if s["block_distractions"] else [])))
    apps = list(dict.fromkeys(s["block_apps"] + (s["distraction_apps"] if s["block_distractions"] else [])))
    nt8 = db.q("SELECT pos_abs FROM live_state WHERE platform='nt8' AND updated>?", (now - 300,))
    return {"locked": bool(lk), "until": lk["until"] if lk else None, "reason": lk["reason"] if lk else "",
            "sites": sites, "apps": apps, "nt8_flat": (all((r["pos_abs"] or 0) == 0 for r in nt8) if nt8 else None),
            "positions_open": sum((v["stats"]["pos_abs"] or 0) for v in st.values()),
            "next_scheduled": next_scheduled(now, s)}


# ---------------------------------------------------------------- scheduled + manual lockouts
def _at(ts, hm, tzname, days=0):
    """Timestamp of HH:MM (local tz) on the calendar day of ts (+days)."""
    from datetime import timedelta
    from clock import local
    d = local(ts, tzname) + timedelta(days=days)
    h, m = map(int, hm.split(":"))
    return d.replace(hour=h, minute=m, second=0, microsecond=0).timestamp()


def lock_end(now, s=None):
    """When a manual / scheduled lockout ends: the next `lock_until` (ET) at least 4 h away, else the reset."""
    s = s or settings()
    if not s.get("lock_until"):
        return next_reset(now, s["reset_time"], s["tz"])
    tz = s.get("lock_until_tz") or s["tz"]
    t = _at(now, s["lock_until"], tz)
    if t <= now:
        t = _at(now, s["lock_until"], tz, days=1)
    if t - now < 4 * 3600:                       # e.g. "done" at 06:00 -> tomorrow 07:00, not in one hour
        t = _at(t, s["lock_until"], tz, days=1)
    return t


def _weekend_window(now, s):
    """(start, end) of the weekend lockout that contains or follows `now`."""
    from clock import local
    d = local(now, s["tz"])
    back = (d.weekday() - 4) % 7                    # days since Friday
    start = _at(now, s["weekend_lock_from"], s["tz"], days=-back)
    utz = s.get("lock_until_tz") or s["tz"]
    end = _at(start + 3 * 86400, s.get("lock_until") or "08:00", utz)   # Monday morning
    if now >= end:
        start = _at(start, s["weekend_lock_from"], s["tz"], days=7)
        end = _at(start + 3 * 86400, s.get("lock_until") or "08:00", utz)
    return start, end


def scheduled_locks(now, s):
    """Create the daily auto-lockout and the weekend lockout when their time comes (idempotent)."""
    from clock import local
    d = local(now, s["tz"])
    made = []
    if s.get("weekend_lock") and s.get("weekend_lock_from"):
        start, end = _weekend_window(now, s)
        tag = local(start, s["tz"]).date().isoformat()
        if start <= now < end and not db.q1("SELECT 1 FROM locks WHERE rule='weekend' AND day=?", (tag,)):
            made.append(("weekend", tag, end, f"Weekend lockout (from Friday {s['weekend_lock_from']} ET)"))
    at = s.get("auto_lock_time")
    if at and d.weekday() < 5:
        start = _at(now, at, s["tz"])
        tag = d.date().isoformat()
        if now >= start and now - start < 12 * 3600 and not db.q1("SELECT 1 FROM locks WHERE rule='auto_daily' AND day=?", (tag,)):
            made.append(("auto_daily", tag, lock_end(start, s), f"Daily auto-lockout at {at} ET"))
    for rule, tag, until, reason in made:
        if until <= now:
            continue
        with db.tx() as c:
            c.execute("INSERT INTO locks(day,account,grp,scope,rule,kind,reason,value,limit_value,started,until,flatten)"
                      " VALUES(?,?,?,?,?,?,?,?,?,?,?,1)", (tag, "schedule", "", "all", rule, "hard", reason, 0, 0, now, until))
            _event(c, now, "", "lock", f"{reason} until {datetime_hm(until, s['tz'])} ET", today(now))


def next_scheduled(now, s):
    """Next scheduled lockout start within 24 h (for the companion's heads-up), or None."""
    out = []
    if s.get("auto_lock_time"):
        for k in (0, 1):
            t = _at(now, s["auto_lock_time"], s["tz"], days=k)
            from clock import local
            if t > now and local(t, s["tz"]).weekday() < 5:
                out.append((t, f"Daily auto-lockout at {s['auto_lock_time']} ET"))
                break
    if s.get("weekend_lock") and s.get("weekend_lock_from"):
        start, _ = _weekend_window(now, s)
        if start > now:
            out.append((start, f"Weekend lockout from Friday {s['weekend_lock_from']} ET"))
    out = [o for o in out if o[0] - now < 24 * 3600]
    return {"at": min(out)[0], "reason": min(out)[1]} if out else None


def lock_intervals():
    """Hard lock windows per account (scope-aware) for flagging trades taken while locked."""
    return db.q("SELECT account, grp, scope, started, until, reason FROM locks WHERE kind='hard'")


def manual_lock(reason="Done for today", minutes=None, flatten=True, now=None, account=None):
    """'Lock me out' button: a hard lock on every account until `lock_until` the next morning (or the
    reset, or N minutes) - or on ONE account when `account` is given (scope 'account': the others keep
    trading). Like every lock, it cannot be lifted early."""
    now = now or time.time()
    s = settings()
    d = trading_day(now, s["reset_time"], s["tz"])
    until = now + float(minutes) * 60 if minutes else lock_end(now, s)
    reason = (str(reason or "").strip() or "Done for today")[:200]
    with db.tx() as c:
        c.execute("INSERT INTO locks(day,account,grp,scope,rule,kind,reason,value,limit_value,started,until,flatten)"
                  " VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                  (d, account or "manual", "", "account" if account else "all", "manual", "hard",
                   f"Manual lockout{' (this account)' if account else ''}: {reason}", 0, 0, now, until, 1 if flatten else 0))
        _event(c, now, account or "", "lock", f"Manual lockout{' of ' + account if account else ''} until {datetime_hm(until, s['tz'])} ET: {reason}", d)
    return status_all(now)


def local_hm(ts, tzname="America/New_York"):
    from clock import local
    return local(ts, tzname).strftime("%H:%M")


def datetime_hm(ts, tzname):
    from clock import local
    return local(ts, tzname).strftime("%a %H:%M")

