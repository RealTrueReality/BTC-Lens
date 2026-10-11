namespace BtcLens.Core;

public sealed record WatchVersion(int Revision, string Name, decimal Lower, decimal Upper,
    string Timeframe, Bias Bias, string Notes, string? SourceZoneId, DateTimeOffset CreatedAt)
{
    public PriceReference? PriceReference { get; init; }
    public ChartAnchor? ChartAnchor { get; init; }
}
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
    public static void Validate(WatchVersion v)
    {
        if (string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 100) throw new ArgumentException("关注区名称为必填，最多 100 字。");
        if (v.Lower <= 0 || v.Upper <= v.Lower) throw new ArgumentException("区域上边界必须大于正数下边界。");
        if (!Timeframes.All.Contains(v.Timeframe)) throw new ArgumentException("周期无效。");
        if (v.Notes.Length > 2000) throw new ArgumentException("备注最多 2000 字。");
    }
    public static bool Inside(WatchVersion z, decimal price) => price >= z.Lower && price <= z.Upper;
    public static bool Entered(WatchZone z, PriceSample current, long now, bool continuity, long maximumSampleGap = 30_000)
    {
        if (z.DeletedAt is not null || !z.Enabled || z.Triggered || !continuity || current.Price <= 0 || current.Timestamp > now + 5000 || now - current.Timestamp > 30_000) return false;
        var previous = z.LastSample;
        if (previous is null || previous.Timestamp < z.Current.CreatedAt.ToUnixTimeMilliseconds() ||
            current.Timestamp <= previous.Timestamp || current.Timestamp - previous.Timestamp > maximumSampleGap) return false;
        return !Inside(z.Current, previous.Price) && Inside(z.Current, current.Price);
    }
    public static string EventId(WatchZone z) => $"{z.Scope}:{z.Id}:{z.Current.Revision}:{Version}";
}
