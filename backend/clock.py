"""Trading-day clock. A trading day runs from the reset time (default 18:00 ET, the CME
Globex reopen) to the next reset; it is named after the calendar date it ends on."""
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

DEFAULT_TZ = "America/New_York"
DEFAULT_RESET = "18:00"


def _hm(s):
    h, m = str(s).split(":")
    return int(h), int(m)


def local(ts, tzname=DEFAULT_TZ):
    return datetime.fromtimestamp(ts, ZoneInfo(tzname))


def trading_day(ts, reset=DEFAULT_RESET, tzname=DEFAULT_TZ):
    d = local(ts, tzname)
    if (d.hour, d.minute) >= _hm(reset):
        d += timedelta(days=1)
    wd = d.weekday()
    if wd == 5:        # Friday evening -> Monday
        d += timedelta(days=2)
    elif wd == 6:      # Sunday evening session belongs to Monday
        d += timedelta(days=1)
    return d.date().isoformat()


def next_reset(ts, reset=DEFAULT_RESET, tzname=DEFAULT_TZ):
    now = local(ts, tzname)
    h, m = _hm(reset)
    r = now.replace(hour=h, minute=m, second=0, microsecond=0)
    if r <= now:
        r += timedelta(days=1)
    return r.timestamp()


def minutes_of_day(ts, tzname=DEFAULT_TZ):
    d = local(ts, tzname)
    return d.hour * 60 + d.minute


def hm_to_min(s):
    if not s:
        return None
    h, m = _hm(s)
    return h * 60 + m


def window_state(ts, start, end, tzname=DEFAULT_TZ):
    """(inside, next_open_ts) for a daily trading window given as 'HH:MM' strings (ET).
    No window configured -> always inside. Weekends are outside."""
    s, e = hm_to_min(start), hm_to_min(end)
    if s is None or e is None:
        return True, None
    d = local(ts, tzname)
    m = d.hour * 60 + d.minute
    if d.weekday() < 5 and s <= m < e:
        return True, None
    # next opening
    nxt = d.replace(hour=s // 60, minute=s % 60, second=0, microsecond=0)
    if nxt <= d:
        nxt += timedelta(days=1)
    while nxt.weekday() >= 5:
        nxt += timedelta(days=1)
    return False, nxt.timestamp()


def fmt_hm(ts, tzname=DEFAULT_TZ):
    return local(ts, tzname).strftime("%H:%M")
