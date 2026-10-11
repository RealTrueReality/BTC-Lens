using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BtcLens.Core;

public sealed record CandleCoverage(long Start, long End, int Expected, int Observed, int Missing,
    int Rejected, int Conflicts, long? First, long? Last)
{
    public bool Complete => Expected > 0 && Observed == Expected && Rejected == 0 && Conflicts == 0;
}
public sealed record ProfileRow(decimal Lower, decimal Upper, decimal VolumeBtc);
public sealed record ProfileResult(string Algorithm, string Instrument, long Start, long End, long KnownAt,
    decimal ContractValue, decimal TickSize, decimal ValueAreaPercent, decimal TotalBtc,
    decimal? Poc, decimal? Vah, decimal? Val, int PocRow, int VaLowRow, int VaHighRow,
    ProfileRow[] Rows, CandleCoverage Coverage, string SourceHash);
public sealed record SavedProfile(string Id, string Scope, string Name, string DisplayTimeframe,
    long CreatedAt, ProfileResult Result, Candle[] SourceBars)
{
    public bool Visible { get; init; } = true;
    public long? DeletedAt { get; init; }
}
public sealed record PriceReference(string Kind, string EvidenceId, string Label, decimal Price,
    long KnownAt, string Algorithm, string SourceHash);
public sealed record VwapPoint(long KnownAt, decimal Value, int Segment);
public sealed record VwapResult(string Period, long Anchor, long KnownAt, int OffsetHours,
    decimal? Value, decimal WeightedSum, decimal VolumeBtc, CandleCoverage Coverage,
    string SourceHash, VwapPoint[] Points)
{
    public decimal ContractValue { get; init; }
}
public sealed record PriceContextView(string Status, string? Error, int OffsetHours,
    VwapResult[] Vwaps, SavedProfile[] Profiles)
{
    public static PriceContextView Empty => new("等待 5m 数据", null, 8, [], []);
    public object Evidence(long cutoff) => new { Status, Error, OffsetHours, algorithm = PriceContextRules.VwapVersion,
        vwaps = Vwaps.Where(v => v.KnownAt <= cutoff).Select(v => new { v.Period, v.Anchor, v.KnownAt, v.OffsetHours, v.Value,
            v.WeightedSum, v.VolumeBtc, v.ContractValue, v.Coverage, v.SourceHash }),
        profiles = Profiles.Where(p => p.CreatedAt <= cutoff && p.Result.End <= cutoff).Select(p => new { p.Id, p.Name, p.CreatedAt, p.Result }) };
}

/// <summary>Independent candle approximations, always using confirmed 5m bars available at cutoff.</summary>
public static class PriceContextRules
{
    public const long Step = 300_000;
    public const string ProfileVersion = "Candle-Uniform-Profile/1.0:VA-CrossTarget:POC-LowerTie";
    public const string VwapVersion = "Closed-5m-HLC3-VWAP/1.0";
    public static readonly string[] Periods = ["day", "week", "month"];
    public static string PeriodName(string period) => period switch { "day" => "日", "week" => "周", "month" => "月", _ => throw new ArgumentException("Invalid period.") };
    public static long Anchor(long timestamp, string period, int offsetHours)
    {
        if (offsetHours is not (0 or 8)) throw new ArgumentException("仅支持 UTC 或 UTC+8。");
        var local = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToOffset(TimeSpan.FromHours(offsetHours));
        var date = local.Date;
        date = period switch { "day" => date, "week" => date.AddDays(-((int)date.DayOfWeek + 6) % 7),
            "month" => new DateTime(date.Year, date.Month, 1), _ => throw new ArgumentException("Invalid period.") };
        return new DateTimeOffset(date, TimeSpan.FromHours(offsetHours)).ToUnixTimeMilliseconds();
    }
    private static (Candle[] Bars, CandleCoverage Coverage) Select(IEnumerable<Candle> input, long start, long end, long cutoff)
    {
        var available = input.Where(c => c.OpenTime >= start && c.OpenTime < end && c.OpenTime + Step <= cutoff && c.Confirmed).ToArray();
        var rejected = available.Count(c => !c.Valid || c.Timeframe != "5m" || c.Instrument != "BTC-USDT-SWAP" || c.OpenTime % Step != 0 || c.OpenTime + Step > end);
        var groups = available.Where(c => c.Valid && c.Timeframe == "5m" && c.Instrument == "BTC-USDT-SWAP" && c.OpenTime % Step == 0 && c.CloseTime <= end)
            .GroupBy(c => c.OpenTime).ToArray();
        var conflicts = groups.Count(g => g.Distinct().Count() > 1);
        var bars = groups.Where(g => g.Distinct().Count() == 1).Select(g => g.First()).OrderBy(c => c.OpenTime).ToArray();
        var expected = checked((int)Math.Max(0, (end - start) / Step));
        return (bars, new(start, end, expected, bars.Length, Math.Max(0, expected - bars.Length), rejected, conflicts,
            bars.FirstOrDefault()?.OpenTime, bars.LastOrDefault()?.CloseTime));
    }
    public static Candle[] ClosedBars(IEnumerable<Candle> input, long start, long end, long cutoff) => Select(input, start, end, cutoff).Bars;
    private static void Validate(InstrumentInfo instrument)
    {
        if (instrument.Id != "BTC-USDT-SWAP" || instrument.ContractCurrency != "BTC" || instrument.ContractValue <= 0 || instrument.TickSize <= 0)
            throw new ArgumentException("需要 BTC 计价的线性永续合约元数据。");
    }
    public static string Hash(IEnumerable<Candle> bars)
    {
        var text = string.Join("\n", bars.Select(c => string.Join("|", c.Instrument, c.Timeframe,
            c.OpenTime.ToString(CultureInfo.InvariantCulture), c.Open.ToString(CultureInfo.InvariantCulture),
            c.High.ToString(CultureInfo.InvariantCulture), c.Low.ToString(CultureInfo.InvariantCulture),
            c.Close.ToString(CultureInfo.InvariantCulture), c.Volume.ToString(CultureInfo.InvariantCulture))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
    public static ProfileResult Profile(IEnumerable<Candle> input, long start, long end, long cutoff,
        InstrumentInfo instrument, int rowCount = 40, decimal valueAreaPercent = 70)
    {
        Validate(instrument);
        if (start % Step != 0 || end % Step != 0 || end <= start || end > cutoff || end - start > 40 * 86_400_000L)
            throw new ArgumentException("选区必须为已结束的 5m 整点，跨度不超过 40 天。");
        if (rowCount is < 8 or > 120 || valueAreaPercent is < 1 or > 100) throw new ArgumentException("行数应为 8–120，价值区比例应为 1–100%。");
        var (bars, coverage) = Select(input, start, end, cutoff);
        var hash = Hash(bars);
        if (bars.Length == 0) return new(ProfileVersion, instrument.Id, start, end, cutoff,
            instrument.ContractValue, instrument.TickSize, valueAreaPercent, 0, null, null, null, -1, -1, -1, [], coverage, hash);
        var lower = Math.Floor(bars.Min(c => c.Low) / instrument.TickSize) * instrument.TickSize;
        var upper = Math.Ceiling(bars.Max(c => c.High) / instrument.TickSize) * instrument.TickSize;
        var width = Math.Max(instrument.TickSize, Math.Ceiling((upper - lower) / rowCount / instrument.TickSize) * instrument.TickSize);
        var count = Math.Max(1, (int)Math.Ceiling((upper - lower) / width));
        var volumes = new decimal[count];
        foreach (var c in bars)
        {
            var volume = c.Volume * instrument.ContractValue;
            if (volume == 0) continue;
            if (c.High == c.Low) { volumes[Math.Clamp((int)((c.Close - lower) / width), 0, count - 1)] += volume; continue; }
            var portions = Enumerable.Range(0, count).Select(i => (i, overlap: Math.Max(0, Math.Min(c.High, lower + (i + 1) * width) - Math.Max(c.Low, lower + i * width))))
                .Where(x => x.overlap > 0).ToArray();
            decimal used = 0;
            for (var j = 0; j < portions.Length; j++)
            {
                var portion = j == portions.Length - 1 ? volume - used : volume * portions[j].overlap / (c.High - c.Low);
                volumes[portions[j].i] += portion; used += portion;
            }
        }
        var total = bars.Sum(c => c.Volume * instrument.ContractValue);
        var lastNonzero = Array.FindLastIndex(volumes, v => v > 0);
        if (lastNonzero >= 0) volumes[lastNonzero] = total - volumes.Take(lastNonzero).Sum();
        var rows = volumes.Select((v, i) => new ProfileRow(lower + i * width, lower + (i + 1) * width, v)).ToArray();
        if (total == 0) return new(ProfileVersion, instrument.Id, start, end, cutoff, instrument.ContractValue, instrument.TickSize,
            valueAreaPercent, 0, null, null, null, -1, -1, -1, rows, coverage, hash);
        // Include whole adjacent rows until the target fraction is met or crossed; this is an explicit original variant.
        var poc = Array.IndexOf(volumes, volumes.Max()); var lo = poc; var hi = poc; var area = volumes[poc];
        while (area < total * valueAreaPercent / 100 && (lo > 0 || hi < count - 1))
        {
            var down = lo > 0 ? volumes[lo - 1] : -1; var up = hi < count - 1 ? volumes[hi + 1] : -1;
            if (up > down || up == down && hi - poc <= poc - lo) area += volumes[++hi];
            else area += volumes[--lo];
        }
        return new(ProfileVersion, instrument.Id, start, end, cutoff, instrument.ContractValue, instrument.TickSize,
            valueAreaPercent, total, (rows[poc].Lower + rows[poc].Upper) / 2, rows[hi].Upper, rows[lo].Lower,
            poc, lo, hi, rows, coverage, hash);
    }
    public static VwapResult Vwap(IEnumerable<Candle> input, long cutoff, InstrumentInfo instrument, string period, int offsetHours)
    {
        Validate(instrument);
        var start = Anchor(cutoff, period, offsetHours); var end = cutoff / Step * Step;
        var (bars, coverage) = Select(input, start, end, cutoff);
        decimal weighted = 0, volume = 0; var points = new List<VwapPoint>(); var segment = 0; long previous = start - Step;
        foreach (var c in bars)
        {
            if (c.OpenTime != previous + Step) segment++;
            var btc = c.Volume * instrument.ContractValue;
            weighted += (c.High + c.Low + c.Close) / 3 * btc; volume += btc; previous = c.OpenTime;
            if (volume > 0) points.Add(new(c.CloseTime, weighted / volume, segment));
        }
        return new(period, start, bars.LastOrDefault()?.CloseTime ?? start, offsetHours,
            volume > 0 ? weighted / volume : null, weighted, volume, coverage, Hash(bars), points.ToArray()) { ContractValue = instrument.ContractValue };
    }
    public static VwapResult[] Vwaps(IEnumerable<Candle> bars, long cutoff, InstrumentInfo instrument, int offsetHours) =>
        Periods.Select(p => Vwap(bars, cutoff, instrument, p, offsetHours)).ToArray();
}
