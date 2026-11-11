"""SQLite storage. One writer lock; WAL so the web UI can read while the guard writes."""
import json
import os
import sqlite3
import threading
from contextlib import contextmanager

HERE = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.environ.get("TG_DATA", os.path.join(HERE, "data"))
DB_PATH = os.path.join(DATA_DIR, "tiltguard.db")

SCHEMA = """
CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT);
CREATE TABLE IF NOT EXISTS sessions (token TEXT PRIMARY KEY, created REAL, expires REAL);
CREATE TABLE IF NOT EXISTS accounts (
  name TEXT PRIMARY KEY, platform TEXT, broker TEXT, broker_id TEXT, env TEXT,
  label TEXT DEFAULT '', firm TEXT DEFAULT '', grp TEXT DEFAULT '', hidden INTEGER DEFAULT 0,
  journal INTEGER DEFAULT 1, first_seen REAL, last_seen REAL);
CREATE TABLE IF NOT EXISTS fills (
  account TEXT, exec_id TEXT, contract TEXT, root TEXT, side INTEGER, qty REAL, price REAL,
  ts REAL, commission REAL, point_value REAL, source TEXT, order_id TEXT, pushed INTEGER DEFAULT 0,
  PRIMARY KEY (account, exec_id));
CREATE INDEX IF NOT EXISTS fills_push ON fills(pushed);
CREATE INDEX IF NOT EXISTS fills_ts ON fills(account, ts);
CREATE TABLE IF NOT EXISTS trades (
  id TEXT PRIMARY KEY, account TEXT, day TEXT, contract TEXT, root TEXT, direction INTEGER,
  qty REAL, entry_ts REAL, exit_ts REAL, entry_price REAL, exit_price REAL, points REAL,
  gross REAL, fees REAL, net REAL, n_fills INTEGER, open INTEGER DEFAULT 0, source TEXT);
CREATE INDEX IF NOT EXISTS trades_day ON trades(day);
CREATE INDEX IF NOT EXISTS trades_acct ON trades(account, day);
CREATE TABLE IF NOT EXISTS live_state (
  account TEXT PRIMARY KEY, day TEXT, updated REAL, platform TEXT, balance REAL, total_pl REAL,
  open_pl REAL, realized_pl REAL, positions TEXT DEFAULT '[]', pos_abs REAL DEFAULT 0,
  working_orders INTEGER DEFAULT 0, peak_pl REAL DEFAULT 0, trough_pl REAL DEFAULT 0, native TEXT DEFAULT '');
CREATE TABLE IF NOT EXISTS locks (
  id INTEGER PRIMARY KEY AUTOINCREMENT, day TEXT, account TEXT, grp TEXT DEFAULT '', scope TEXT,
  rule TEXT, kind TEXT, reason TEXT, value REAL, limit_value REAL, started REAL, until REAL,
  flatten INTEGER DEFAULT 0);
CREATE INDEX IF NOT EXISTS locks_until ON locks(until);
CREATE TABLE IF NOT EXISTS events (
  id INTEGER PRIMARY KEY AUTOINCREMENT, ts REAL, day TEXT, account TEXT, type TEXT, detail TEXT);
CREATE INDEX IF NOT EXISTS events_day ON events(day);
"""

MIGRATIONS = [("accounts", "journal", "INTEGER DEFAULT 1"), ("fills", "pushed", "INTEGER DEFAULT 0"),
              ("live_state", "native", "TEXT DEFAULT ''")]

_lock = threading.RLock()
_conn = None


def conn():
    global _conn
    if _conn is None:
        os.makedirs(DATA_DIR, exist_ok=True)
        c = sqlite3.connect(DB_PATH, check_same_thread=False, isolation_level=None, timeout=30)
        c.row_factory = sqlite3.Row
        c.execute("PRAGMA journal_mode=WAL")
        c.execute("PRAGMA synchronous=NORMAL")
        for table, col, ddl in MIGRATIONS:
            try:
                if col not in {r[1] for r in c.execute(f"PRAGMA table_info({table})")}:
                    c.execute(f"ALTER TABLE {table} ADD COLUMN {col} {ddl}")
            except sqlite3.OperationalError:
                pass  # table not created yet
        c.executescript(SCHEMA)
        _conn = c
    return _conn


@contextmanager
def tx():
    """Serialised write transaction."""
    with _lock:
        c = conn()
        c.execute("BEGIN IMMEDIATE")
        try:
            yield c
            c.execute("COMMIT")
        except Exception:
            c.execute("ROLLBACK")
            raise


def q(sql, args=()):
    with _lock:
        return [dict(r) for r in conn().execute(sql, args).fetchall()]


def q1(sql, args=()):
    rows = q(sql, args)
    return rows[0] if rows else None


def kv_get(k, default=None):
    r = q1("SELECT v FROM kv WHERE k=?", (k,))
    return json.loads(r["v"]) if r else default


def kv_set(k, v, c=None):
    sql, args = "INSERT INTO kv(k,v) VALUES(?,?) ON CONFLICT(k) DO UPDATE SET v=excluded.v", (k, json.dumps(v))
    if c is not None:
        c.execute(sql, args)
    else:
        with tx() as c2:
            c2.execute(sql, args)
