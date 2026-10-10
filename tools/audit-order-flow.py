"""Read-only independent audit of committed CVD records using decimal arithmetic.

Usage: python tools/audit-order-flow.py path/to/btclens.db
Run against a stopped or demo-mode instance for a consistent collection checkpoint.
"""
import json
import sqlite3
import sys
from decimal import Decimal
from pathlib import Path

path = Path(sys.argv[1]).resolve()
with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True) as db:
    db.execute("BEGIN")
    load = lambda value: json.loads(value, parse_float=Decimal)
    segments = [load(row[0]) for row in db.execute("SELECT Json FROM flow_segments")]
    messages = 0
    for segment in segments:
        records = [load(row[0]) for row in db.execute("SELECT Json FROM captured_trades WHERE SegmentId=?", (segment["id"],))]
        buy, sell, matches = Decimal(0), Decimal(0), 0
        for record in records:
            trade = record["trade"]
            expected = Decimal(trade["size"])
            if trade["instrument"] == "BTC-USDT-SWAP":
                assert segment["contract"]["contractCurrency"] == "BTC"
                expected *= Decimal(segment["contract"]["contractValue"])
            assert expected == Decimal(record["quantityBtc"])
            assert record["id"] == trade["instrument"] + ":" + str(trade["tradeId"])
            if trade["side"] == "buy":
                buy += expected
            else:
                assert trade["side"] == "sell"
                sell += expected
            matches += trade["count"]
        assert len(records) == segment["messages"]
        assert matches == segment["matches"]
        assert buy == Decimal(segment["buyBtc"])
        assert sell == Decimal(segment["sellBtc"])
        assert buy - sell == Decimal(segment["cvdBtc"])
        points = [load(row[0]) for row in db.execute("SELECT Json FROM cvd_points WHERE Id LIKE ?", (segment["id"] + ":%",))]
        if points:
            latest = max(points, key=lambda p: p["timestamp"])
            assert Decimal(latest["cvdBtc"]) == buy - sell
            assert latest["timestamp"] == segment["lastTradeAt"]
            assert sum((Decimal(p["buyBtc"]) for p in points), Decimal(0)) == buy
            assert sum((Decimal(p["sellBtc"]) for p in points), Decimal(0)) == sell
        messages += len(records)
    oi_count = db.execute("SELECT COUNT(*) FROM open_interest").fetchone()[0]
    print(json.dumps({"verified": True, "segments": len(segments), "aggregateMessages": messages,
        "oiSamples": oi_count, "checks": ["independent BTC conversion", "raw messages vs segment totals",
        "aggregate match counts", "minute buckets vs final CVD", "exact decimal arithmetic"]}, indent=2))
