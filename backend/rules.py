"""Tilt rules: definitions, tighten/loosen classification and evaluation (pure functions).

Anti-tilt contract: a stricter value applies immediately; a looser value applies immediately only
before the first trade of the day (and with no active lock), otherwise at the next session reset."""
import math

from clock import hm_to_min, window_state

RULES = [
    # key, label, kind ('lower' = smaller is stricter, 'higher', 'bool', 'time'), unit, help
    ("max_daily_loss", "Daily loss limit", "lower", "$",
     "Lock + flatten when today's P&L (closed + open) reaches -X."),
    ("profit_target", "Daily profit target", "lower", "$",
     "Lock + flatten when today's P&L reaches +X, so a green day stays green."),
    ("giveback_pct", "Max give-back from peak", "lower", "%",
     "Lock when P&L falls this % below the day's peak (only once the peak is above the minimum)."),
    ("giveback_min_peak", "Give-back arms at peak", "lower", "$",
     "The give-back rule only watches days whose peak P&L passed this amount."),
    ("max_trades", "Max trades per day", "lower", "#", "Lock after this many closed trades."),
    ("max_consec_losses", "Max losses in a row", "lower", "#",
     "Lock after this many consecutive losing trades."),
    ("cooldown_after_loss", "Cooldown after a loss", "higher", "min",
     "Block new entries for N minutes after every losing trade."),
    ("max_trade_loss", "Max loss per trade", "lower", "$",
     "Flatten a position whose open loss reaches -X (a hard stop you can't move)."),
    ("max_contracts", "Max contracts", "lower", "#",
     "Reject orders that would take the position above N contracts."),
    ("window_start", "Trading window start (ET)", "time", "",
     "New entries only inside the window. Exits are always allowed."),
    ("window_end", "Trading window end (ET)", "time", "", ""),
    ("flatten_on_lock", "Flatten when locked", "bool", "",
     "Close open positions and cancel entry orders when a lock starts."),
]
KEYS = [r[0] for r in RULES]
KIND = {r[0]: r[2] for r in RULES}

DEFAULT_RULES = {
    "max_daily_loss": None, "profit_target": None, "giveback_pct": None, "giveback_min_peak": None,
    "max_trades": None, "max_consec_losses": None, "cooldown_after_loss": None,
    "max_trade_loss": None, "max_contracts": None, "window_start": None, "window_end": None,
    "flatten_on_lock": True,
}


def clean(rules):
    """Normalise user input: '' / 0 / None -> None (off), numbers -> float, times -> 'HH:MM'."""
    out = {}
    for k, v in (rules or {}).items():
        if k not in KIND:
            continue
        kind = KIND[k]
        if kind == "bool":
            out[k] = bool(v)
        elif kind == "time":
            v = (v or "").strip() if isinstance(v, str) else v
            if not v:
                out[k] = None
            else:
                h, m = str(v).split(":")[:2]
                h, m = int(h), int(m)
                if not (0 <= h < 24 and 0 <= m < 60):
                    raise ValueError(f"{k}: bad time {v}")
                out[k] = f"{h:02d}:{m:02d}"
        else:
            if v in (None, "", 0, "0"):
                out[k] = None
            else:
                f = float(v)
                if f < 0 or math.isnan(f):
                    raise ValueError(f"{k}: must be positive")
                out[k] = f if f else None
    return out


def _num(v, off):
    return off if v in (None, 0) else float(v)


def compare(key, old, new):
    """-1 stricter, +1 looser, 0 same."""
    kind = KIND[key]
    if kind == "lower":
        o, n = _num(old, math.inf), _num(new, math.inf)
        return (n > o) - (n < o)
    if kind == "higher":
        o, n = _num(old, 0), _num(new, 0)
        return (n < o) - (n > o)
    if kind == "bool":
        return (bool(old) and not new) - (bool(new) and not old)
    return 0  # times are compared as a window pair


def _window(r):
    s, e = hm_to_min(r.get("window_start")), hm_to_min(r.get("window_end"))
    return (0, 24 * 60) if s is None or e is None else (s, e)


def split_changes(current, proposed, loosen_now):
    """-> (apply_now, pending) given the current effective rules and a proposed partial update."""
    now, later = {}, {}
    for k, v in proposed.items():
        if KIND[k] == "time":
            continue
        c = compare(k, current.get(k), v)
        if c == 0:
            continue
        (now if c < 0 or loosen_now else later)[k] = v
    if "window_start" in proposed or "window_end" in proposed:
        new = {"window_start": proposed.get("window_start", current.get("window_start")),
               "window_end": proposed.get("window_end", current.get("window_end"))}
        (os_, oe), (ns, ne) = _window(current), _window(new)
        if (ns, ne) != (os_, oe):
            stricter = ns >= os_ and ne <= oe and ns < ne
            (now if stricter or loosen_now else later).update(new)
    return now, later


def money(x):
    return f"-${abs(x):,.0f}" if x < 0 else f"${x:,.0f}"


def evaluate(r, ctx, now, tzname="America/New_York"):
    """r: effective rules. ctx: day_pl, open_pl, peak_pl, pos_abs, closed (list of dicts with net,
    exit_ts, in order). Returns {'hard': [...], 'cooldown': [...], 'flatten': [...], 'window': {...}|None}.
    Each breach: rule, reason, value, limit (+ until for cooldowns)."""
    out = {"hard": [], "cooldown": [], "flatten": [], "window": None}
    day_pl, open_pl, peak = ctx.get("day_pl") or 0.0, ctx.get("open_pl") or 0.0, ctx.get("peak_pl") or 0.0
    closed = ctx.get("closed") or []

    lim = r.get("max_daily_loss")
    if lim and day_pl <= -lim:
        out["hard"].append({"rule": "max_daily_loss", "value": day_pl, "limit": -lim,
                            "reason": f"Daily loss limit hit: {money(day_pl)} (limit {money(-lim)})"})
    tgt = r.get("profit_target")
    if tgt and day_pl >= tgt:
        out["hard"].append({"rule": "profit_target", "value": day_pl, "limit": tgt,
                            "reason": f"Profit target reached: {money(day_pl)} (target {money(tgt)}). Day done."})
    pct, arm = r.get("giveback_pct"), r.get("giveback_min_peak") or 0
    if pct and peak > 0 and peak >= arm and day_pl <= peak * (1 - pct / 100):
        out["hard"].append({"rule": "giveback_pct", "value": day_pl, "limit": peak * (1 - pct / 100),
                            "reason": f"Gave back {pct:.0f}% of the day's peak: {money(peak)} -> {money(day_pl)}"})
    n = r.get("max_trades")
    if n and len(closed) >= n:
        out["hard"].append({"rule": "max_trades", "value": len(closed), "limit": n,
                            "reason": f"Max trades reached: {len(closed)}/{int(n)}"})
    streak = 0
    for t in reversed(closed):
        if (t.get("net") or 0) < 0:
            streak += 1
        else:
            break
    cl = r.get("max_consec_losses")
    if cl and streak >= cl:
        out["hard"].append({"rule": "max_consec_losses", "value": streak, "limit": cl,
                            "reason": f"{streak} losing trades in a row (limit {int(cl)})"})
    cd = r.get("cooldown_after_loss")
    if cd and closed and (closed[-1].get("net") or 0) < 0:
        until = closed[-1]["exit_ts"] + cd * 60
        if now < until:
            out["cooldown"].append({"rule": "cooldown_after_loss", "value": closed[-1]["net"], "limit": cd,
                                    "until": until, "reason": f"Cooldown after a loss ({int(cd)} min)"})
    mtl = r.get("max_trade_loss")
    if mtl and ctx.get("pos_abs") and open_pl <= -mtl:
        out["flatten"].append({"rule": "max_trade_loss", "value": open_pl, "limit": -mtl,
                               "reason": f"Max loss per trade: open {money(open_pl)} (limit {money(-mtl)})"})
    mc = r.get("max_contracts")
    if mc and (ctx.get("pos_abs") or 0) > mc:
        out["flatten"].append({"rule": "max_contracts", "value": ctx["pos_abs"], "limit": mc,
                               "reason": f"Position {ctx['pos_abs']:g} contracts > max {mc:g}"})
    inside, nxt = window_state(now, r.get("window_start"), r.get("window_end"), tzname)
    if not inside:
        out["window"] = {"rule": "window", "until": nxt,
                         "reason": f"Outside your trading window {r['window_start']}-{r['window_end']} ET"}
    ctx["streak"] = streak
    return out
