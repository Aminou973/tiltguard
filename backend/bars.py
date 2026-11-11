"""NQ / ES 1-minute candles for an optional journal market-data provider.

Reads a local bar archive (TG_BARS_DIR/{NQ,ES}_1m.parquet) and serves it to the journal
running on the same host. Local-only: the endpoint refuses anything that came through the reverse proxy."""
import os
import threading

import numpy as np

BARS_DIR = os.environ.get("TG_BARS_DIR", os.path.join(os.path.dirname(__file__), "data", "bars"))
STEPS = {"1m": 60_000, "5m": 300_000, "15m": 900_000, "1h": 3_600_000, "1d": 86_400_000}
ROOTS = {"NQ": "NQ", "MNQ": "NQ", "ES": "ES", "MES": "ES"}
MAX_ROWS = 20_000
_cache, _lock = {}, threading.Lock()


def _path(root):
    return os.path.join(BARS_DIR, f"{root}_1m.parquet")


def _load(root):
    """-> (ts_ms int64[], ohlcv float64[n,5]); cached until the file changes."""
    import pyarrow as pa
    import pyarrow.parquet as pq
    path = _path(root)
    mtime = os.path.getmtime(path)
    with _lock:
        hit = _cache.get(root)
        if hit and hit[0] == mtime:
            return hit[1], hit[2]
        t = pq.read_table(path)
        tcol = next(n for n, f in zip(t.column_names, t.schema) if pa.types.is_timestamp(f.type))
        ts = t.column(tcol).cast(pa.timestamp("ms", tz="UTC")).to_numpy().astype("datetime64[ms]").astype(np.int64)
        vals = np.column_stack([t.column(c).to_numpy().astype(np.float64)
                                for c in ("open", "high", "low", "close", "volume")])
        order = np.argsort(ts, kind="stable")
        ts, vals = ts[order], vals[order]
        _cache[root] = (mtime, ts, vals)
        return ts, vals


def history(symbol, res, start_ms, end_ms):
    root = ROOTS.get(str(symbol).upper())
    if not root:
        raise ValueError("symbol must be NQ, MNQ, ES or MES")
    step = STEPS.get(res)
    if not step:
        raise ValueError("res must be one of " + ", ".join(STEPS))
    ts, vals = _load(root)
    i0, i1 = np.searchsorted(ts, start_ms, "left"), np.searchsorted(ts, end_ms, "left")
    ts, vals = ts[i0:i1], vals[i0:i1]
    if step > 60_000 and len(ts):
        bucket = (ts // step) * step
        starts = np.flatnonzero(np.r_[True, bucket[1:] != bucket[:-1]])
        ends = np.r_[starts[1:], len(ts)] - 1
        ts = bucket[starts]
        vals = np.column_stack([vals[starts, 0], np.maximum.reduceat(vals[:, 1], starts),
                                np.minimum.reduceat(vals[:, 2], starts), vals[ends, 3],
                                np.add.reduceat(vals[:, 4], starts)])
    rows = [[int(t), *(round(float(x), 4) for x in v)] for t, v in zip(ts[:MAX_ROWS], vals[:MAX_ROWS])]
    return {"symbol": root, "res": res, "bars": rows, "truncated": len(ts) > MAX_ROWS}


def health():
    out = {}
    for root in ("NQ", "ES"):
        try:
            ts, _ = _load(root)
            out[root] = {"rows": int(len(ts)), "from": int(ts[0]), "to": int(ts[-1])}
        except Exception as e:  # missing archive
            out[root] = {"error": str(e)[:200]}
    return {"ok": True, "symbols": out}
