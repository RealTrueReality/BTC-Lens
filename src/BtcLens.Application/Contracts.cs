using BtcLens.Core;

namespace BtcLens.Application;

public interface ILensStore
{
    string? GetSetting(string key);
    void SetSetting(string key, string value);
    WatchZone[] Watches(string scope, bool includeDeleted = false);
    void SaveWatch(WatchZone zone, int? promotedDrawingRevision = null);
    bool DeleteWatch(string scope, string id, int expectedRevision, DateTimeOffset deletedAt);
    AlertEvent[] Alerts(string scope);
    AlertEvent? FindAlert(string id);
    bool RecordAlert(WatchZone updated, AlertEvent alert);
    Trade[] Trades(string mode);
    Trade? FindTrade(string id);
    void SaveTrade(Trade trade);
    int ImportTrades(IReadOnlyList<Trade> trades);
    void SaveCandles(string scope, IEnumerable<Candle> candles);
    Candle[] ReadCandles(string scope);
    Candle[] ReadCandles(string scope, long start, long end);
    SavedProfile[] Profiles(string scope);
    void SaveProfile(SavedProfile profile);
    bool SetProfileState(string scope, string id, bool visible, long? deletedAt);
    ChartDrawing[] Drawings(string scope);
    void SaveDrawing(ChartDrawing drawing, int? expectedRevision);
    bool DeleteDrawing(string scope, string id, int expectedRevision, long deletedAt);
    FlowSegment[] FlowSegments(int limit = 40);
    CvdPoint[] CvdPoints(long since, int limit = 3000);
    OpenInterestSample[] OpenInterest(long since, int limit = 3000);
    CapturedTrade? FindCapturedTrade(string id);
    CapturedTrade[] CapturedTrades(string segmentId, int limit = 500);
    FlowCapture? CaptureFlow(string segmentId, int limit = 500);
    void SaveFlowBatch(FlowSegment[] segments, CvdPoint[] points, CapturedTrade[] trades);
    void SaveOpenInterest(OpenInterestSample sample);
}

public sealed record ImportPreview(Trade[] Trades, string[] Errors);
public sealed record AnalysisPacket(string GeneratedAt, string Mode, string Transport,
    string DataStatus, string[] Observations, string[] Missing, string[] NextSteps)
{
    public object? PriceContext { get; init; }
}

public static class AnalysisReport
{
    public static AnalysisPacket Build(MarketView market, WatchZone[] watches)
    {
        var facts = new List<string>();
        if (market.Quote is not null) facts.Add($"BTC-USDT-SWAP 最新观测价 {market.Quote.Price:0.0}；价格时间 {DateTimeOffset.FromUnixTimeMilliseconds(market.Quote.Timestamp):O}。");
        foreach (var w in watches.Where(w => w.Enabled).Take(8))
            facts.Add($"关注区「{w.Current.Name}」{w.Current.Timeframe}，{w.Current.Lower:0.0}–{w.Current.Upper:0.0}，版本 {w.Current.Revision}；{(w.Triggered ? "已记录首次到区" : "等待新的到区事件")}。");
        foreach (var z in market.Zones.Where(z => z.Life == ZoneLife.Active).OrderByDescending(z => z.KnownAt).Take(6))
            facts.Add($"{z.Timeframe} {z.Kind} {z.Bias}，{z.Lower:0.0}–{z.Upper:0.0}，确认时间 {DateTimeOffset.FromUnixTimeMilliseconds(z.KnownAt):O}，算法 {z.Algorithm}；证据 ID {z.Id}。");
        var missing = new List<string> { "主目标及其来源资格尚未人工确认。", "Sweep、SFP 与有效 MSB 的个人规则尚待逐项定义。" };
        foreach (var v in market.PriceContext.Vwaps)
        {
            var label = PriceContextRules.PeriodName(v.Period) + " VWAP";
            if (v.Value is not null && v.Coverage.Complete && v.KnownAt <= market.Cutoff &&
                v.Anchor == PriceContextRules.Anchor(market.Cutoff, v.Period, v.OffsetHours) && market.Cutoff - v.KnownAt < 600_000)
                facts.Add($"{label} {v.Value:0.0}，UTC+{v.OffsetHours} 重置；5m 收盘 HLC3 × BTC 成交量近似，覆盖 {v.Coverage.Observed}/{v.Coverage.Expected}，截至 {DateTimeOffset.FromUnixTimeMilliseconds(v.KnownAt):O}；来源摘要 {v.SourceHash}。" );
            else missing.Add($"{label} 当前锚点数据不完整、过期或无成交量；覆盖 {v.Coverage.Observed}/{v.Coverage.Expected}，不能作为完整周期参考。");
        }
        foreach (var p in market.PriceContext.Profiles.Where(p => p.CreatedAt <= market.Cutoff && p.Result.End <= market.Cutoff).Take(4))
        {
            var r = p.Result;
            facts.Add($"固定范围「{p.Name}」POC {r.Poc:0.0} / VAH {r.Vah:0.0} / VAL {r.Val:0.0}，范围 {DateTimeOffset.FromUnixTimeMilliseconds(r.Start):O} 至 {DateTimeOffset.FromUnixTimeMilliseconds(r.End):O}；5m 区间均匀分配近似，覆盖 {r.Coverage.Observed}/{r.Coverage.Expected}，证据 ID {p.Id}，算法 {r.Algorithm}。");
            if (!r.Coverage.Complete) missing.Add($"固定范围「{p.Name}」存在缺失或冲突数据；计算仅代表已覆盖部分。");
        }
        if (market.PriceContext.Error is not null) missing.Add(market.PriceContext.Error);
        foreach (var instrument in FlowRules.Instruments)
        {
            var segment = market.Flow.Latest(instrument);
            if (market.Mode == "live" && segment is { Messages: > 0, EndedAt: null } && segment.LastTradeAt <= market.Cutoff && market.Cutoff - segment.LastTradeAt <= 30_000)
                facts.Add($"{instrument} 观测成交 CVD {segment.CvdBtc:0.########} BTC，累计自 {DateTimeOffset.FromUnixTimeMilliseconds(segment.StartedAt):O}，段 ID {segment.Id}；仅 OKX 单品种，缺口前后分别累计。");
            else missing.Add($"{instrument} CVD 未采集、已过期或为合成演示，不能用作实盘确认。");
        }
        if (market.Mode == "live" && market.Flow.OpenInterest.LastOrDefault() is { } oi && oi.Timestamp <= market.Cutoff && market.Cutoff - oi.Timestamp <= 30_000)
            facts.Add($"BTC-USDT-SWAP OI {oi.Contracts:0.########} 张 / {oi.CoinBtc:0.########} BTC / {oi.Usd:0.##} USD；时间 {DateTimeOffset.FromUnixTimeMilliseconds(oi.Timestamp):O}。OI 本身不确定多空方向。");
        else missing.Add("永续 OI 未采集、已过期或为合成演示。");
        return new(DateTimeOffset.FromUnixTimeMilliseconds(market.Cutoff).ToString("O"), market.Mode, market.Transport, market.Status,
            facts.ToArray(), missing.ToArray(),
            ["在日线/4H 确认关注区和目标的不同角色。", "对照选区 VAH/POC/VAL 与日/周/月 VWAP，记录价格所处位置。", "价格到区后观察 15m/5m 反应，记录支持与冲突证据。", "目标、确认与失效依据齐全后，再填写候选交易计划。"])
            { PriceContext = market.PriceContext.Evidence(market.Cutoff) };
    }
}
