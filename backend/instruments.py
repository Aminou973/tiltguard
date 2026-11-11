"""Futures contract helpers: root symbol parsing and $ point values."""
import re

POINT_VALUES = {
    "NQ": 20, "MNQ": 2, "ES": 50, "MES": 5, "YM": 5, "MYM": 0.5, "RTY": 50, "M2K": 5,
    "EMD": 100, "NKD": 5, "CL": 1000, "MCL": 100, "QM": 500, "NG": 10000, "QG": 2500,
    "RB": 42000, "HO": 42000, "GC": 100, "MGC": 10, "SI": 5000, "SIL": 1000, "HG": 25000,
    "MHG": 2500, "PL": 50, "PA": 100, "ZB": 1000, "UB": 1000, "ZN": 1000, "ZF": 1000,
    "ZT": 2000, "6E": 125000, "M6E": 12500, "6B": 62500, "M6B": 6250, "6J": 12500000,
    "6A": 100000, "M6A": 10000, "6C": 100000, "6S": 125000, "BTC": 5, "MBT": 0.1,
    "ETH": 50, "MET": 0.1, "ZC": 50, "ZS": 50, "ZW": 50, "ZL": 600, "ZM": 100,
    "LE": 400, "HE": 400, "GF": 500, "VX": 1000,
}

_RX = re.compile(r"^([A-Z0-9]{1,4}?)([FGHJKMNQUVXZ])(\d{1,4})$")


def root_of(symbol):
    s = (symbol or "").upper().strip()
    if ":" in s:
        s = s.split(":", 1)[1]
    s = s.split(" ")[0]           # NinjaTrader "NQ 12-26"
    s = re.sub(r"\d!$", "", s)     # TradingView continuous "NQ1!"
    m = _RX.match(s)
    return m.group(1) if m else s


def point_value(root, given=None):
    if given:
        try:
            v = float(given)
            if v > 0:
                return v
        except (TypeError, ValueError):
            pass
    return float(POINT_VALUES.get(root, 1.0))


FIRM_PREFIXES = [
    ("PAAPEX", "Apex"), ("APEX", "Apex"), ("MFFU", "MyFundedFutures"), ("FTDFY", "Tradeify"),
    ("TDFY", "Tradeify"), ("GFT", "Goat Funded Futures"), ("FNFT", "FundedNext"),
    ("LFE", "Lucid"), ("LFF", "Lucid"), ("LTE", "Lucid"), ("LTD", "Lucid"), ("TPT", "Take Profit Trader"),
    ("TOPSTEP", "Topstep"), ("PTLOP", "Tradeify"), ("SIM", "Simulator"), ("AOTMA", "Earn2Trade"),
]


def guess_firm(name):
    n = (name or "").upper()
    for p, firm in FIRM_PREFIXES:
        if n.startswith(p):
            return firm
    return ""
