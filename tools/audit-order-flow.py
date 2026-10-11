"""Read-only independent audit of committed CVD records using decimal arithmetic.

Usage: python tools/audit-order-flow.py path/to/btclens.db
Reads one SQLite transaction for a consistent collection checkpoint.
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
        buy, sell, buy_usdt, sell_usdt, matches = Decimal(0), Decimal(0), Decimal(0), Decimal(0), 0
        exchange = segment.get("exchange", "OKX")
        for record in records:
            trade = record["trade"]
            expected = Decimal(trade["size"])
            assert trade.get("exchange", "OKX") == exchange
            if trade["instrument"] == "BTC-USDT-SWAP" and exchange == "OKX":
                assert segment["contract"]["contractCurrency"] == "BTC"
                expected *= Decimal(segment["contract"]["contractValue"])
            assert expected == Decimal(record["quantityBtc"])
            assert record["id"] == ("" if exchange == "OKX" else exchange + ":") + trade["instrument"] + ":" + str(trade["tradeId"])
            if exchange == "Binance" and trade.get("rawJson"):
                raw = load(trade["rawJson"])
                assert Decimal(raw["q"]) == expected
                assert Decimal(raw["p"]) == Decimal(trade["price"])
                assert raw["l"] == trade["tradeId"] and raw["l"] - raw["f"] + 1 == trade["count"]
                assert ("sell" if raw["m"] else "buy") == trade["side"]
                assert raw["a"] == trade["aggregateId"]
            if trade["side"] == "buy":
                buy += expected
                buy_usdt += expected * Decimal(trade["price"])
            else:
                assert trade["side"] == "sell"
                sell += expected
                sell_usdt += expected * Decimal(trade["price"])
            matches += trade["count"]
        assert len(records) == segment["messages"]
        assert matches == segment["matches"]
        assert buy == Decimal(segment["buyBtc"])
        assert sell == Decimal(segment["sellBtc"])
        assert buy - sell == Decimal(segment["cvdBtc"])
        if segment.get("buyUsdt") is not None:
            assert buy_usdt == Decimal(segment["buyUsdt"])
            assert sell_usdt == Decimal(segment["sellUsdt"])
        points = [load(row[0]) for row in db.execute("SELECT Json FROM cvd_points WHERE Id LIKE ?", (segment["id"] + ":%",))]
        if points:
            latest = max(points, key=lambda p: p["timestamp"])
            assert Decimal(latest["cvdBtc"]) == buy - sell
            assert latest["timestamp"] == segment["lastTradeAt"]
            assert sum((Decimal(p["buyBtc"]) for p in points), Decimal(0)) == buy
            assert sum((Decimal(p["sellBtc"]) for p in points), Decimal(0)) == sell
            if latest.get("cvdUsdt") is not None:
                assert Decimal(latest["cvdUsdt"]) == buy_usdt - sell_usdt
                assert sum((Decimal(p["buyUsdt"]) for p in points), Decimal(0)) == buy_usdt
                assert sum((Decimal(p["sellUsdt"]) for p in points), Decimal(0)) == sell_usdt
        messages += len(records)
    oi_count = db.execute("SELECT COUNT(*) FROM open_interest").fetchone()[0]
    print(json.dumps({"verified": True, "segments": len(segments), "aggregateMessages": messages,
        "oiSamples": oi_count, "checks": ["independent BTC conversion", "raw messages vs segment totals",
        "aggregate match counts", "minute buckets vs final CVD", "exact decimal arithmetic", "exchange namespaces", "USDT at each execution price"]}, indent=2))
