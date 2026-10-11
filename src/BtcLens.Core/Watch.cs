namespace BtcLens.Core;

public sealed record WatchVersion(int Revision, string Name, decimal Lower, decimal Upper,
    string Timeframe, Bias Bias, string Notes, string? SourceZoneId, DateTimeOffset CreatedAt)
{
    public PriceReference? PriceReference { get; init; }
    public ChartAnchor? ChartAnchor { get; init; }
    public TrendWatch? Trend { get; init; }
}
public sealed record TrendWatch(long Start, long End, decimal Price1, decimal Price2, decimal Tolerance)
{
    public decimal PriceAt(long time) => Price1 + (Price2 - Price1) * ((decimal)time - Start) / ((decimal)End - Start);
}
public sealed record WatchBounds(decimal Lower, decimal Upper, decimal? Center = null);
public sealed record WatchZone(string Id, string Scope, bool Enabled, bool Triggered,
    WatchVersion Current, WatchVersion[] History, PriceSample? LastSample = null)
{
    public DateTimeOffset? DeletedAt { get; init; }
}
public sealed record AlertEvent(string Id, string Scope, string WatchId, int Revision,
    DateTimeOffset CreatedAt, decimal Price, string Transport, WatchVersion Zone,
    string Summary, string EvidenceJson);

public static class WatchRules
{
    public const string Version = "FirstObservedEntry/1.0";
    public const string TrendVersion = "FirstObservedTrendApproachOrCross/1.0";
    public static void Validate(WatchVersion v)
    {
        if (string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 100) throw new ArgumentException("关注区名称为必填，最多 100 字。");
        if (v.Lower <= 0 || v.Upper <= v.Lower) throw new ArgumentException("区域上边界必须大于正数下边界。");
        if (!Timeframes.All.Contains(v.Timeframe)) throw new ArgumentException("周期无效。");
        if (v.Notes.Length > 2000) throw new ArgumentException("备注最多 2000 字。");
        if (v.Trend is { } t && (t.Start <= 0 || t.End <= t.Start || t.Price1 <= 0 || t.Price2 <= 0 ||
            t.Tolerance <= 0 || t.Tolerance >= Math.Min(t.Price1, t.Price2)))
            throw new ArgumentException("趋势线需要两个不同时间的正价格端点；关注距离须为正数且小于端点价格。");
    }
    public static WatchBounds? Bounds(WatchVersion z, long time)
    {
        if (z.Trend is not { } t) return new(z.Lower, z.Upper);
        if (time < t.Start) return null;
        var center = t.PriceAt(time);
        return center <= 0 ? null : new(Math.Max(0, center - t.Tolerance), center + t.Tolerance, center);
    }
    public static bool Inside(WatchVersion z, decimal price, long? time = null) =>
        (z.Trend is null ? new WatchBounds(z.Lower, z.Upper) : time is long ts ? Bounds(z, ts) : null) is { } b && price >= b.Lower && price <= b.Upper;
    public static bool Entered(WatchZone z, PriceSample current, long now, bool continuity, long maximumSampleGap = 30_000)
        => Transition(z, current, now, continuity, maximumSampleGap) is not null;
    public static string? Transition(WatchZone z, PriceSample current, long now, bool continuity, long maximumSampleGap = 30_000)
    {
        if (z.DeletedAt is not null || !z.Enabled || z.Triggered || !continuity || current.Price <= 0 || current.Timestamp > now + 5000 || now - current.Timestamp > 30_000) return null;
        var previous = z.LastSample;
        if (previous is null || previous.Timestamp < z.Current.CreatedAt.ToUnixTimeMilliseconds() ||
            previous.Price <= 0 || current.Timestamp <= previous.Timestamp || current.Timestamp - previous.Timestamp > maximumSampleGap) return null;
        var before = Bounds(z.Current, previous.Timestamp); var after = Bounds(z.Current, current.Timestamp);
        if (before is null || after is null) return null;
        if (z.Current.Trend is not null && (previous.Price < before.Center && current.Price >= after.Center || previous.Price > before.Center && current.Price <= after.Center)) return "cross";
        return !Inside(z.Current, previous.Price, previous.Timestamp) && Inside(z.Current, current.Price, current.Timestamp) ? "approach" : null;
    }
    public static string RuleVersion(WatchVersion z) => z.Trend is null ? Version : TrendVersion;
    public static string EventId(WatchZone z) => $"{z.Scope}:{z.Id}:{z.Current.Revision}:{RuleVersion(z.Current)}";
}
