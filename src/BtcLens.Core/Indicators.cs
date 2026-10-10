namespace BtcLens.Core;

/// <summary>Original reference detectors. Every result is available only after its source bars close.</summary>
public static class Indicators
{
    public const string FvgVersion = "Reference-FVG/1.0";
    public const string ObVersion = "Reference-OB/1.0:N5";

    public static DetectedZone[] Detect(IEnumerable<Candle> input, long cutoff, int pivotSize = 5)
    {
        if (pivotSize < 1) throw new ArgumentOutOfRangeException(nameof(pivotSize));
        var bars = input.Where(c => c.Valid && c.Confirmed && c.CloseTime <= cutoff)
            .GroupBy(c => c.OpenTime).Select(g => g.Last()).OrderBy(c => c.OpenTime).ToArray();
        if (bars.Length == 0) return [];
        if (bars.Any(c => c.Instrument != bars[0].Instrument || c.Timeframe != bars[0].Timeframe))
            throw new ArgumentException("Detector input must contain one instrument/timeframe.");
        var zones = new List<DetectedZone>();
        int? highPivot = null, lowPivot = null;
        var consumed = new HashSet<(int, Bias)>();
        for (var i = 0; i < bars.Length; i++)
        {
            // Do not bridge missing bars: each independently continuous segment warms up again.
            var segmentStart = i;
            while (segmentStart > 0 && bars[segmentStart - 1].CloseTime == bars[segmentStart].OpenTime) segmentStart--;
            if (i == segmentStart) { highPivot = null; lowPivot = null; }
            if (i - segmentStart >= 2)
            {
                var a = bars[i - 2]; var c = bars[i];
                if (c.Low > a.High) Add(i, i - 2, ZoneKind.Fvg, Bias.Bullish, a.High, c.Low, FvgVersion);
                if (c.High < a.Low) Add(i, i - 2, ZoneKind.Fvg, Bias.Bearish, c.High, a.Low, FvgVersion);
            }
            var p = i - pivotSize;
            if (p - pivotSize >= segmentStart)
            {
                if (Enumerable.Range(p - pivotSize, pivotSize * 2 + 1).Where(x => x != p).All(x => bars[p].High > bars[x].High)) highPivot = p;
                if (Enumerable.Range(p - pivotSize, pivotSize * 2 + 1).Where(x => x != p).All(x => bars[p].Low < bars[x].Low)) lowPivot = p;
            }
            if (i > segmentStart)
            {
                if (highPivot is int hp && bars[i - 1].Close <= bars[hp].High && bars[i].Close > bars[hp].High)
                    TryOb(i, hp, Bias.Bullish);
                if (lowPivot is int lp && bars[i - 1].Close >= bars[lp].Low && bars[i].Close < bars[lp].Low)
                    TryOb(i, lp, Bias.Bearish);
            }
        }
        return zones.ToArray();

        void TryOb(int i, int pivot, Bias bias)
        {
            if (!consumed.Add((pivot, bias))) return;
            for (var s = i - 1; s >= pivot; s--)
            {
                if (bias == Bias.Bullish ? bars[s].Close < bars[s].Open : bars[s].Close > bars[s].Open)
                { Add(i, s, ZoneKind.OrderBlock, bias, bars[s].Low, bars[s].High, $"Reference-OB/1.0:N{pivotSize}"); return; }
            }
        }
        void Add(int formed, int source, ZoneKind kind, Bias bias, decimal lower, decimal upper, string algorithm)
        {
            if (upper <= lower) return;
            var c = bars[formed];
            var life = ZoneLife.Active; long? changed = null;
            for (var j = formed + 1; j < bars.Length; j++)
            {
                if (bars[j - 1].CloseTime != bars[j].OpenTime) { life = ZoneLife.Unknown; changed = bars[j].CloseTime; break; }
                var b = bars[j];
                var ended = kind == ZoneKind.Fvg
                    ? (bias == Bias.Bullish ? b.Low <= lower : b.High >= upper)
                    : (bias == Bias.Bullish ? b.Close < lower : b.Close > upper);
                if (ended) { life = kind == ZoneKind.Fvg ? ZoneLife.Filled : ZoneLife.Invalidated; changed = b.CloseTime; break; }
                if (b.High >= lower && b.Low <= upper && life == ZoneLife.Active) { life = ZoneLife.Touched; changed = b.CloseTime; }
            }
            zones.Add(new($"{c.Instrument}:{c.Timeframe}:{algorithm}:{bias}:{c.OpenTime}", c.Instrument,
                c.Timeframe, kind, bias, lower, upper, bars[source].OpenTime, c.CloseTime, algorithm, life, changed));
        }
    }

    public static KeyLevel[] Levels(IReadOnlyDictionary<string, Candle[]> input, long cutoff)
    {
        var result = new List<KeyLevel>();
        var dayStart = (long)Math.Floor((cutoff + 28_800_000L) / 86_400_000d) * 86_400_000L - 28_800_000L;
        var weekStart = (long)Math.Floor((cutoff - 316_800_000L) / 604_800_000d) * 604_800_000L + 316_800_000L;
        if (input.TryGetValue("1D", out var days))
        {
            var previous = days.LastOrDefault(c => c.Valid && c.Confirmed && c.OpenTime == dayStart - 86_400_000L && c.CloseTime <= cutoff);
            if (previous is not null)
            { result.Add(new("前日高", previous.High, "OKX 原生 1D / UTC+8", previous.CloseTime)); result.Add(new("前日低", previous.Low, "OKX 原生 1D / UTC+8", previous.CloseTime)); }
            var current = days.LastOrDefault(c => c.Valid && c.OpenTime == dayStart && c.OpenTime <= cutoff);
            if (current is not null) result.Add(new("当日开盘", current.Open, "OKX 原生 1D / UTC+8", current.OpenTime));
        }
        if (input.TryGetValue("1W", out var weeks))
        {
            var previous = weeks.LastOrDefault(c => c.Valid && c.Confirmed && c.OpenTime == weekStart - 604_800_000L && c.CloseTime <= cutoff);
            if (previous is not null)
            { result.Add(new("前周高", previous.High, "OKX 原生 1W / UTC+8", previous.CloseTime)); result.Add(new("前周低", previous.Low, "OKX 原生 1W / UTC+8", previous.CloseTime)); }
        }
        return result.ToArray();
    }
}
