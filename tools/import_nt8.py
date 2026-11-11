"""Import NinjaTrader 8 execution history into Tiltguard (re-runnable: fills are de-duplicated).

  python import_nt8.py --server http://127.0.0.1:8940 --key tg_xxx
  python import_nt8.py --since 2026-01-01 --accounts DEMOACCT0002,DEMOACCT0003

Reads Documents/NinjaTrader 8/db/NinjaTrader.sqlite read-only. Times in that DB are UTC .NET ticks."""
import argparse
import json
import os
import sqlite3
import sys
import urllib.request
from datetime import datetime, timezone

DEFAULT_DB = os.path.join(os.path.expanduser("~"), "Documents", "NinjaTrader 8", "db", "NinjaTrader.sqlite")
EPOCH_TICKS = 621355968000000000
SKIP = {"Backtest", "Playback101"}


def ticks_to_unix(t):
    return (t - EPOCH_TICKS) / 1e7


def month_code(expiry_ticks):
    if not expiry_ticks:
        return ""
    d = datetime.fromtimestamp(ticks_to_unix(expiry_ticks), timezone.utc)
    return f"{d.month:02d}-{d.year % 100:02d}"


def load(db_path, since=None, accounts=None):
    c = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    sql = ("SELECT a.Name acct, e.ExecutionId, e.OrderId, e.MarketPosition, e.Quantity, e.Price, e.Commission, e.Fee,"
           " e.Time, m.Name root, m.PointValue, i.Expiry FROM Executions e JOIN Accounts a ON a.Id=e.Account"
           " JOIN Instruments i ON i.Id=e.Instrument JOIN MasterInstruments m ON m.Id=i.MasterInstrument")
    fills = []
    for acct, eid, oid, mp, qty, px, comm, fee, t, root, pv, exp in c.execute(sql):
        if acct in SKIP or (accounts and acct not in accounts):
            continue
        ts = ticks_to_unix(t)
        if since and ts < since:
            continue
        fills.append({"account": acct, "exec_id": eid or f"{oid}-{t}", "order_id": oid, "side": 1 if mp == 0 else -1,
                      "qty": qty, "price": px, "ts": ts, "commission": (comm or 0) + (fee or 0),
                      "contract": f"{root} {month_code(exp)}".strip(), "root": root, "point_value": pv})
    return fills


def post(server, key, fills, batch=3000):
    total = 0
    for i in range(0, len(fills), batch):
        body = json.dumps({"platform": "nt8", "fills": fills[i:i + batch]}).encode()
        req = urllib.request.Request(server.rstrip("/") + "/api/ingest/fills", data=body, method="POST",
                                     headers={"Content-Type": "application/json", "X-TG-Key": key})
        with urllib.request.urlopen(req, timeout=300) as r:
            res = json.loads(r.read())
        total += res.get("received", 0)
        print(f"  sent {min(i + batch, len(fills))}/{len(fills)}  (accounts rebuilt: {res.get('accounts_rebuilt')})")
    return total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", default=os.environ.get("TG_SERVER", "http://127.0.0.1:8940"))
    ap.add_argument("--key", default=os.environ.get("TG_KEY"))
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--since", help="YYYY-MM-DD")
    ap.add_argument("--accounts", help="comma separated account names")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    since = datetime.fromisoformat(a.since).replace(tzinfo=timezone.utc).timestamp() if a.since else None
    fills = load(a.db, since, set(a.accounts.split(",")) if a.accounts else None)
    accts = sorted({f["account"] for f in fills})
    print(f"{len(fills)} fills from {len(accts)} accounts")
    if a.dry_run:
        return
    if not a.key:
        sys.exit("--key (or TG_KEY) required - copy it from the journal Settings page")
    post(a.server, a.key, fills)
    print("done")


if __name__ == "__main__":
    main()
