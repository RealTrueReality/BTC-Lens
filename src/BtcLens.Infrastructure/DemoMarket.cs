using BtcLens.Core;

namespace BtcLens.Infrastructure;

/// <summary>Synthetic, deterministic OHLCV. Higher timeframes only aggregate the revealed prefix.</summary>
public static class DemoMarket
{
    public const int InitialIndex = 5940;
    public static Candle[] Generate()
    {
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        var bars = new List<Candle>(); decimal previous = 64800;
        for (var i = 0; i < 6000; i++)
        {
            var close = (decimal)(64800 + i * .35 + Math.Sin(i / 70d) * 540 + Math.Sin(i / 9d) * 90);
            if (i >= 5700 && i < InitialIndex) close = (decimal)(67000 + Math.Sin((i - 5700) / 12d) * 230 + Math.Cos(i / 3d) * 20);
            if (i == InitialIndex - 1) close = 67200;
            if (i >= InitialIndex) { var j = i - InitialIndex + 1; close = (decimal)(67200 - Math.Min(j, 8) * 30 + Math.Sin(j / 2d) * 18); }
            close = Math.Round(close, 1);
            var wick = 7m + i % 11;
            bars.Add(new("BTC-USDT-SWAP", "5m", start + i * 300_000L, previous,
                Math.Max(previous, close) + wick, Math.Min(previous, close) - wick, close, 100 + i % 67, true));
            previous = close;
        }
        return bars.ToArray();
    }
    public static Dictionary<string, Candle[]> Visible(Candle[] source, int index)
    {
        var prefix = source.Take(index).ToArray();
        var cutoff = prefix[^1].CloseTime;
        return Timeframes.All.Append("1W").ToDictionary(tf => tf, tf =>
            prefix.GroupBy(c => Bucket(c.OpenTime, tf)).Select(g => new Candle("BTC-USDT-SWAP", tf, g.Key,
                g.First().Open, g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close, g.Sum(c => c.Volume),
                g.Key + Timeframes.Milliseconds(tf) <= cutoff && g.First().OpenTime == g.Key))
            .TakeLast(300).ToArray());
    }
    private static long Bucket(long time, string tf)
    {
        // OKX native periods use UTC+8; weeks start Monday 00:00 UTC+8.
        var anchor = tf == "1W" ? 316_800_000L : -28_800_000L;
        var size = Timeframes.Milliseconds(tf);
        return (long)Math.Floor((time - anchor) / (double)size) * size + anchor;
    }
}
