"""Read an exported frozen profile and recompute it independently using Decimal."""
import json, sys, hashlib
from decimal import Decimal, getcontext, ROUND_CEILING, ROUND_FLOOR
getcontext().prec = 60
with open(sys.argv[1], encoding="utf-8-sig") as f:
    profile = json.load(f, parse_float=Decimal)
r, bars = profile["result"], profile["sourceBars"]
dec = lambda x: Decimal(str(x))
lines = ["|".join(str(c[k]) for k in ("instrument", "timeframe", "openTime", "open", "high", "low", "close", "volume")) for c in bars]
assert hashlib.sha256("\n".join(lines).encode()).hexdigest() == r["sourceHash"]
assert all(c["confirmed"] and c["openTime"] >= r["start"] and c["openTime"] + 300000 <= r["end"] <= r["knownAt"] for c in bars)
rows = r["rows"]
volumes = [Decimal(0)] * len(rows)
expected_total = sum(dec(c["volume"]) * dec(r["contractValue"]) for c in bars)
for c in bars:
    volume, high, low = dec(c["volume"]) * dec(r["contractValue"]), dec(c["high"]), dec(c["low"])
    if high == low:
        index = next((i for i, row in enumerate(rows) if dec(row["lower"]) <= low < dec(row["upper"])), len(rows)-1)
        volumes[index] += volume
    else:
        for i, row in enumerate(rows):
            overlap = max(Decimal(0), min(high, dec(row["upper"])) - max(low, dec(row["lower"])))
            volumes[i] += volume * overlap / (high - low)
assert expected_total == dec(r["totalBtc"])
assert abs(sum(dec(row["volumeBtc"]) for row in rows) - expected_total) < Decimal("1e-20")
assert all(abs(v - dec(row["volumeBtc"])) < Decimal("1e-20") for v, row in zip(volumes, rows))
poc = max(range(len(rows)), key=lambda i: (dec(rows[i]["volumeBtc"]), -i))
assert poc == r["pocRow"]
assert (dec(rows[poc]["lower"]) + dec(rows[poc]["upper"]))/2 == dec(r["poc"])
lo, hi = r["vaLowRow"], r["vaHighRow"]
assert dec(rows[lo]["lower"]) == dec(r["val"]) and dec(rows[hi]["upper"]) == dec(r["vah"])
assert sum(dec(row["volumeBtc"]) for row in rows[lo:hi+1]) >= expected_total * dec(r["valueAreaPercent"]) / 100
print(json.dumps({"source_bars":len(bars), "rows":len(rows), "total_btc":str(expected_total), "source_hash":"verified", "distribution":"independent Decimal match", "cutoff":"verified"}))
if len(sys.argv) == 4:
    import sqlite3
    from pathlib import Path
    with open(sys.argv[2], encoding="utf-8-sig") as f:
        analysis = json.load(f, parse_float=Decimal)
    with sqlite3.connect(Path(sys.argv[3]).resolve().as_uri() + "?mode=ro", uri=True) as db:
        raw = [json.loads(row[0], parse_float=Decimal) for row in db.execute("SELECT Json FROM candle_cache WHERE Scope = ?", ("live:analysis",))]
    checks = []
    for v in analysis["priceContext"]["vwaps"]:
        selected = sorted((c for c in raw if c["confirmed"] and v["coverage"]["start"] <= c["openTime"] and c["openTime"] + 300000 <= v["coverage"]["end"]), key=lambda c: c["openTime"])
        source = "\n".join("|".join(str(c[k]) for k in ("instrument", "timeframe", "openTime", "open", "high", "low", "close", "volume")) for c in selected)
        assert hashlib.sha256(source.encode()).hexdigest() == v["sourceHash"]
        volume = sum(dec(c["volume"]) * dec(v["contractValue"]) for c in selected)
        weighted = sum((dec(c["high"]) + dec(c["low"]) + dec(c["close"])) / 3 * dec(c["volume"]) * dec(v["contractValue"]) for c in selected)
        assert len(selected) == v["coverage"]["observed"] == v["coverage"]["expected"]
        assert volume == dec(v["volumeBtc"])
        assert abs(weighted - dec(v["weightedSum"])) < Decimal("1e-15")
        assert abs(weighted / volume - dec(v["value"])) < Decimal("1e-18")
        checks.append({"period": v["period"], "bars":len(selected), "vwap":"independent Decimal match", "hash":"verified"})
    print(json.dumps({"vwaps":checks}))
