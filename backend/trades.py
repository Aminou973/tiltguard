"""Fills -> round-trip trades. A trade opens when the position leaves flat and closes when
it returns to flat; a fill that flips the position closes one trade and opens the next."""
import hashlib

from clock import trading_day

EPS = 1e-9


def _tid(account, key):
    return hashlib.sha1(f"{account}|{key}".encode()).hexdigest()[:16]


def build_trades(fills, fee_per_side=None, reset="18:00", tzname="America/New_York"):
    """fills: dicts with account, exec_id, contract, root, side (+1 buy / -1 sell), qty, price,
    ts (epoch seconds), commission, point_value — all for ONE account. fee_per_side(root) -> $ per
    contract per side, used when a fill carries no commission."""
    fee_per_side = fee_per_side or (lambda root: 0.0)
    by_contract = {}
    for f in sorted(fills, key=lambda f: (f["ts"], str(f["exec_id"]))):
        by_contract.setdefault(f["contract"], []).append(f)

    out = []
    for contract, fs in by_contract.items():
        pos, cur = 0.0, None
        for f in fs:
            q, side, px = float(f["qty"]), int(f["side"]), float(f["price"])
            fill_fee = float(f.get("commission") or 0)
            per_unit_fee = fill_fee / q if fill_fee > 0 and q > 0 else fee_per_side(f["root"])
            part = 0
            while q > EPS:
                if abs(pos) < EPS:
                    pos = 0.0
                    cur = {
                        "id": _tid(f["account"], f"{f['exec_id']}:{part}"),
                        "account": f["account"], "contract": contract, "root": f["root"],
                        "direction": side, "point_value": float(f["point_value"]),
                        "entry_ts": f["ts"], "exit_ts": None, "entry_qty": 0.0, "entry_val": 0.0,
                        "exit_qty": 0.0, "exit_val": 0.0, "fees": 0.0, "n_fills": 0, "max_pos": 0.0,
                        "source": f.get("source", ""),
                    }
                if pos == 0 or (pos > 0) == (side > 0):          # open / add
                    take = q
                    cur["entry_qty"] += take
                    cur["entry_val"] += take * px
                else:                                              # reduce / close
                    take = min(q, abs(pos))
                    cur["exit_qty"] += take
                    cur["exit_val"] += take * px
                cur["fees"] += take * per_unit_fee
                cur["n_fills"] += 1
                pos += side * take
                q -= take
                part += 1
                cur["max_pos"] = max(cur["max_pos"], abs(pos))
                if abs(pos) < EPS:
                    cur["exit_ts"] = f["ts"]
                    out.append(_finish(cur, reset, tzname))
                    cur, pos = None, 0.0
        if cur is not None:
            out.append(_finish(cur, reset, tzname, open_=True))
    out.sort(key=lambda t: t["entry_ts"])
    return out


def _finish(c, reset, tzname, open_=False):
    d = c["direction"]
    entry = c["entry_val"] / c["entry_qty"] if c["entry_qty"] else None
    t = {
        "id": c["id"], "account": c["account"], "contract": c["contract"], "root": c["root"],
        "direction": d, "qty": c["max_pos"], "entry_ts": c["entry_ts"], "exit_ts": c["exit_ts"],
        "entry_price": entry, "n_fills": c["n_fills"], "fees": round(c["fees"], 2),
        "source": c["source"], "open": 1 if open_ else 0,
    }
    if open_:
        t.update(exit_price=None, points=None, gross=None, net=None,
                 day=trading_day(c["entry_ts"], reset, tzname))
    else:
        exit_ = c["exit_val"] / c["exit_qty"]
        gross = (c["exit_val"] - c["entry_val"]) * d * c["point_value"]
        t.update(exit_price=exit_, points=(exit_ - entry) * d, gross=round(gross, 2),
                 net=round(gross - c["fees"], 2), day=trading_day(c["exit_ts"], reset, tzname))
    return t
