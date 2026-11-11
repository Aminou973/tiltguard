"""Core tests: python tests/test_core.py (uses a throwaway DB)."""
import os
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.environ["TG_DATA"] = tempfile.mkdtemp(prefix="tg_test_")

import db  # noqa: E402
import guard  # noqa: E402
import rules as R  # noqa: E402
from clock import next_reset, trading_day, window_state  # noqa: E402
from instruments import root_of  # noqa: E402
from trades import build_trades  # noqa: E402

# 2026-09-24 (Thursday) 10:00 ET = 14:00 UTC
T0 = 1790258400.0


def F(eid, side, qty, price, ts, contract="MNQZ6", acct="A1", comm=0.0):
    return {"account": acct, "exec_id": eid, "contract": contract, "root": root_of(contract), "side": side,
            "qty": qty, "price": price, "ts": ts, "commission": comm, "point_value": 2.0}


def test_roots():
    assert root_of("MNQZ6") == "MNQ" and root_of("NQZ6") == "NQ" and root_of("MESZ6") == "MES"
    assert root_of("M2KZ6") == "M2K" and root_of("6EZ6") == "6E" and root_of("NQ 12-26") == "NQ"
    assert root_of("CME_MINI:NQZ2026") == "NQ" and root_of("NQ1!") == "NQ" and root_of("GCZ6") == "GC"


def test_clock():
    assert trading_day(T0) == "2026-09-24"
    assert trading_day(T0 + 8.5 * 3600) == "2026-09-25"         # 18:30 ET -> next day
    fri_eve = 1790460000.0 + 3600                                # Fri 2026-09-26? use offset check below
    assert trading_day(T0 + 2 * 86400 + 9 * 3600) == "2026-09-28"  # Sat 19:00 ET -> Monday
    r = next_reset(T0)
    assert r - T0 == 8 * 3600
    assert window_state(T0, "09:30", "11:30")[0] is True
    assert window_state(T0 + 2 * 3600, "09:30", "11:30")[0] is False


def test_trades_simple_and_scale():
    fills = [F("1", 1, 2, 100.0, T0), F("2", 1, 1, 102.0, T0 + 10), F("3", -1, 3, 105.0, T0 + 60)]
    t = build_trades(fills)
    assert len(t) == 1 and t[0]["qty"] == 3 and not t[0]["open"]
    # entry avg 100.667, exit 105 -> (315 - 302) * 2 = 26
    assert abs(t[0]["gross"] - 26.0) < 1e-6, t[0]


def test_trades_reversal_and_fees():
    fills = [F("1", 1, 1, 100.0, T0, comm=0.5), F("2", -1, 3, 98.0, T0 + 30, comm=1.5), F("3", 1, 2, 97.0, T0 + 90)]
    t = build_trades(fills, lambda r: 0.25)
    assert len(t) == 2
    a, b = t
    assert a["direction"] == 1 and abs(a["gross"] - (-4.0)) < 1e-9 and abs(a["fees"] - 1.0) < 1e-9
    assert b["direction"] == -1 and b["qty"] == 2 and abs(b["gross"] - 4.0) < 1e-9
    assert abs(b["fees"] - (1.0 + 0.5)) < 1e-9, b      # 2 x 0.5 from fill 2, 2 x 0.25 default for fill 3
    assert a["id"] != b["id"]


def test_trades_open():
    t = build_trades([F("1", -1, 1, 100.0, T0)])
    assert t[0]["open"] == 1 and t[0]["net"] is None


def test_rule_compare_and_split():
    cur = {"max_daily_loss": 500, "max_trades": None, "cooldown_after_loss": 5, "window_start": "09:30",
           "window_end": "11:30", "flatten_on_lock": True}
    now, later = R.split_changes(cur, R.clean({"max_daily_loss": 400, "max_trades": 3, "cooldown_after_loss": 2,
                                                "window_end": "12:00", "flatten_on_lock": False}), False)
    assert now == {"max_daily_loss": 400.0, "max_trades": 3.0}, now
    assert later["cooldown_after_loss"] == 2.0 and later["window_end"] == "12:00" and later["flatten_on_lock"] is False
    now, later = R.split_changes(cur, R.clean({"max_daily_loss": 0}), False)   # disabling = looser
    assert later == {"max_daily_loss": None} and not now
    now, later = R.split_changes(cur, R.clean({"max_daily_loss": 900}), True)  # before first trade: immediate
    assert now == {"max_daily_loss": 900.0}


def test_evaluate():
    r = dict(R.DEFAULT_RULES, max_daily_loss=500, max_trades=3, max_consec_losses=2, cooldown_after_loss=10,
             giveback_pct=50, giveback_min_peak=300, max_trade_loss=200)
    closed = [{"net": 50, "exit_ts": T0}, {"net": -40, "exit_ts": T0 + 60}, {"net": -60, "exit_ts": T0 + 120}]
    out = R.evaluate(r, {"day_pl": -510, "open_pl": -250, "peak_pl": 50, "pos_abs": 1, "closed": closed}, T0 + 200)
    rules_hit = {b["rule"] for b in out["hard"]}
    assert rules_hit == {"max_daily_loss", "max_trades", "max_consec_losses"}, rules_hit
    assert out["cooldown"] and out["cooldown"][0]["until"] == T0 + 120 + 600
    assert out["flatten"][0]["rule"] == "max_trade_loss"
    out = R.evaluate(r, {"day_pl": 150, "peak_pl": 400, "closed": []}, T0)
    assert [b["rule"] for b in out["hard"]] == ["giveback_pct"]


def test_guard_flow():
    guard.update_rules("default", {"max_daily_loss": 300, "max_trades": 2}, now=T0 - 3600)
    fills = [F("g1", 1, 1, 100.0, T0 - 600, acct="TV1"), F("g2", -1, 1, 50.0, T0 - 500, acct="TV1")]  # -100 $
    st = guard.ingest({"platform": "tv", "accounts": [{"name": "TV1", "total_pl": -100.0, "open_pl": 0,
                                                       "positions": []}], "fills": fills}, now=T0)
    a = st["accounts"]["TV1"]
    assert not a["locked"] and a["stats"]["trades"] == 1 and a["stats"]["loss_left"] == 200.0, a
    # loosening now is refused (already traded) -> pending
    res = guard.update_rules("default", {"max_daily_loss": 1000}, now=T0 + 1)
    assert res["pending"] == {"max_daily_loss": 1000.0} and not res["applied"]
    # open position loses: total -320 -> lock + flatten
    st = guard.ingest({"platform": "tv", "accounts": [{"name": "TV1", "total_pl": -320.0, "open_pl": -220.0,
                                                       "positions": [{"contract": "MNQZ6", "qty": 1}]}]}, now=T0 + 5)
    a = st["accounts"]["TV1"]
    assert a["locked"] and a["flatten"] and "Daily loss" in a["reason"], a
    # default scope 'account': the other account keeps trading, and the PC is not locked
    st = guard.ingest({"platform": "nt8", "accounts": [{"name": "NT2", "positions": []}]}, now=T0 + 6)
    assert not st["accounts"]["NT2"]["locked"] and not st["desktop"]["locked"]
    # loosening is judged per account: NT2 hasn't traded, TV1 is locked
    assert guard.loosen_allowed(T0 + 7, "NT2")[0] and not guard.loosen_allowed(T0 + 7, "TV1")[0]
    # every visible account locked -> PC lockout (until the first of them unlocks)
    db.kv_set("rules", {"default": dict(guard.effective_rules()), "accounts": {"NT2": {"max_trades": 0.5}}})
    st = guard.ingest({"platform": "nt8", "accounts": [{"name": "NT2", "positions": []}],
                       "fills": [F("n1", 1, 1, 100.0, T0 - 60, acct="NT2"), F("n2", -1, 1, 101.0, T0 - 30, acct="NT2")]}, now=T0 + 8)
    assert all(v["locked"] for v in st["accounts"].values()), {k: v["locked"] for k, v in st["accounts"].items()}
    assert st["desktop"]["locked"] and "All accounts locked" in st["desktop"]["reason"], st["desktop"]
    # P&L recovers after flatten - still locked (never unlocks early)
    st = guard.ingest({"platform": "tv", "accounts": [{"name": "TV1", "total_pl": 10.0, "open_pl": 0,
                                                       "positions": []}]}, now=T0 + 60)
    assert st["accounts"]["TV1"]["locked"] and not st["accounts"]["TV1"]["flatten"]
    # next session: lock expired, pending looser limit applied
    nxt = next_reset(T0) + 3600
    st = guard.ingest({"platform": "tv", "accounts": [{"name": "TV1", "total_pl": 0.0, "positions": []}]}, now=nxt)
    assert not st["accounts"]["TV1"]["locked"]
    assert guard.effective_rules()["max_daily_loss"] == 1000.0



def test_hyperprop_and_manual_lock():
    T1 = next_reset(T0) + 2 * 86400 + 3600          # a fresh session, no locks
    guard.update_rules("default", {"max_daily_loss": 300}, now=T1)
    st = guard.ingest({"platform": "hyperprop", "accounts": [{"name": "DEMOACCT0001", "total_pl": -120.0, "open_pl": -20.0,
                       "positions": [{"contract": "MNQZ6", "qty": -2}], "native": {"settings_locked": False, "trades_today": 1}}]}, now=T1)
    a = st["accounts"]["DEMOACCT0001"]
    assert a["stats"]["day_pl"] == -120.0 and not a["locked"] and a["native"]["trades_today"] == 1, a
    assert {"max_trades", "window_start", "window_end", "cooldown_after_loss"} <= set(a["rules"])   # full rule set for clients
    st = guard.manual_lock("Done for today", now=T1 + 60)
    a = st["accounts"]["DEMOACCT0001"]
    assert a["locked"] and a["lock"]["rule"] == "manual" and a["flatten"] and a["lock"]["until"] == guard.lock_end(T1 + 60), a
    assert guard.local_hm(a["lock"]["until"]) == "08:00" and st["desktop"]["locked"] and "tradingview.com" in st["desktop"]["sites"]
    assert "Discord.exe" in st["desktop"]["apps"] and st["block_sites"] == st["desktop"]["sites"]
    st = guard.ingest({"platform": "hyperprop", "accounts": [{"name": "DEMOACCT0001", "total_pl": -120.0, "positions": []}]},
                      now=a["lock"]["until"] + 60)
    assert not st["accounts"]["DEMOACCT0001"]["locked"] and not st["desktop"]["locked"]


def test_lock_end_and_schedules():
    from datetime import datetime
    from zoneinfo import ZoneInfo
    ny = ZoneInfo("America/New_York")
    ts = lambda *a: datetime(*a, tzinfo=ny).timestamp()
    s = dict(guard.settings(), lock_until="08:00")
    assert guard.lock_end(ts(2026, 10, 7, 11, 30), s) == ts(2026, 10, 8, 8, 0)     # done at 11:30 -> tomorrow 08:00
    assert guard.lock_end(ts(2026, 10, 7, 7, 0), s) == ts(2026, 10, 8, 8, 0)       # done at 07:00 -> not today 08:00
    assert guard.lock_end(ts(2026, 10, 7, 20, 0), s) == ts(2026, 10, 8, 8, 0)      # evening -> next morning
    paris = ZoneInfo("Europe/Paris")
    sp = dict(guard.settings(), lock_until="07:00", lock_until_tz="Europe/Paris")
    end = guard.lock_end(datetime(2026, 10, 5, 17, 19, tzinfo=paris).timestamp(), sp)      # Done for today at 17:19 Paris
    assert end == datetime(2026, 10, 6, 7, 0, tzinfo=paris).timestamp(), datetime.fromtimestamp(end, paris)
    db.kv_set("settings", {"auto_lock_time": "11:30", "weekend_lock": True, "weekend_lock_from": "12:00", "lock_until": "08:00"})
    st = guard.status_all(ts(2026, 10, 7, 11, 0))                                   # Wednesday before the auto-lock
    assert not st["desktop"]["locked"] and st["desktop"]["next_scheduled"]["at"] == ts(2026, 10, 7, 11, 30)
    st = guard.status_all(ts(2026, 10, 7, 11, 31))
    assert st["desktop"]["locked"] and "auto-lockout" in st["desktop"]["reason"] and st["desktop"]["until"] == ts(2026, 10, 8, 8, 0)
    st = guard.status_all(ts(2026, 10, 7, 11, 40))
    assert len(db.q("SELECT * FROM locks WHERE rule='auto_daily'")) == 1           # idempotent
    st = guard.status_all(ts(2026, 10, 9, 12, 5))                                   # Friday 12:05 -> weekend lock
    assert st["desktop"]["locked"] and st["desktop"]["until"] == ts(2026, 10, 12, 8, 0), st["desktop"]
    st = guard.status_all(ts(2026, 10, 11, 15, 0))                                  # Sunday afternoon: still locked
    assert st["desktop"]["locked"] and len(db.q("SELECT * FROM locks WHERE rule='weekend'")) == 1
    st = guard.status_all(ts(2026, 10, 12, 8, 1))                                   # Monday 08:01: free
    assert not st["desktop"]["locked"]
    db.kv_set("settings", {})


if __name__ == "__main__":
    n = 0
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            n += 1
            print("ok ", name)
    print(f"{n} tests passed")


def test_manual_lock_one_account():
    T2 = next_reset(T0) + 7 * 86400 + 3600          # a fresh weekday session (no weekend lockout), no locks
    with db.tx() as c:                              # earlier tests leave scheduled all-account lockouts behind
        c.execute("DELETE FROM locks")
    s = guard.settings(); db.kv_set("settings", {**s, "schedules": [], "lock_until": ""}) if "schedules" in s else None
    guard.update_rules("default", {"max_daily_loss": 300}, now=T2)
    accts = [{"name": "DEMOACCT0001", "total_pl": 0.0, "positions": []}, {"name": "DEMOACCT0002", "total_pl": 0.0, "positions": []}]
    guard.ingest({"platform": "hyperprop", "accounts": accts[:1]}, now=T2)
    guard.ingest({"platform": "tv", "accounts": accts[1:]}, now=T2)
    st = guard.manual_lock("Tilted on MFF", minutes=60, account="DEMOACCT0001", now=T2 + 30)
    a, b = st["accounts"]["DEMOACCT0001"], st["accounts"]["DEMOACCT0002"]
    assert a["locked"] and a["lock"]["scope"] == "account" and abs(a["lock"]["until"] - (T2 + 30 + 3600)) < 1, a
    assert not b["locked"] and not st["desktop"]["locked"], (b, st["desktop"])     # the other account keeps trading
    st = guard.ingest({"platform": "tv", "accounts": accts[1:]}, now=T2 + 3700)
    assert not st["accounts"]["DEMOACCT0001"]["locked"]                     # timed lock over


