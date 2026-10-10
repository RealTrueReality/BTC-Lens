using BtcLens.Core;

namespace BtcLens.Application;

public interface ILensStore
{
    string? GetSetting(string key);
    void SetSetting(string key, string value);
    WatchZone[] Watches(string scope);
    void SaveWatch(WatchZone zone);
    AlertEvent[] Alerts(string scope);
    AlertEvent? FindAlert(string id);
    bool RecordAlert(WatchZone updated, AlertEvent alert);
    Trade[] Trades(string mode);
    Trade? FindTrade(string id);
    void SaveTrade(Trade trade);
    int ImportTrades(IReadOnlyList<Trade> trades);
    void SaveCandles(string scope, IEnumerable<Candle> candles);
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
    string DataStatus, string[] Observations, string[] Missing, string[] NextSteps);

public static class AnalysisReport
{
    public static AnalysisPacket Build(MarketView market, WatchZone[] watches)
    {
        var facts = new List<string>();
        if (market.Quote is not null) facts.Add($"BTC-USDT-SWAP 最新观测价 {market.Quote.Price:0.0}；价格时间 {DateTimeOffset.FromUnixTimeMilliseconds(market.Quote.Timestamp):O}。");
        foreach (var w in watches.Where(w => w.Enabled).Take(8))
            facts.Add($"关注区「{w.Current.Name}」{w.Current.Timeframe}，{w.Current.Lower:0.0}–{w.Current.Upper:0.0}，版本 {w.Current.Revision}；{(w.Triggered ? "已记录首次到区" : "等待新的到区事件")}。");
        foreach (var z in market.Zones.Where(z => z.Life is ZoneLife.Active or ZoneLife.Touched).OrderByDescending(z => z.KnownAt).Take(6))
            facts.Add($"{z.Timeframe} {z.Kind} {z.Bias}，{z.Lower:0.0}–{z.Upper:0.0}，确认时间 {DateTimeOffset.FromUnixTimeMilliseconds(z.KnownAt):O}，算法 {z.Algorithm}；证据 ID {z.Id}。");
        var missing = new List<string> { "主目标及其来源资格尚未人工确认。", "Sweep、SFP 与有效 MSB 的个人规则尚待逐项定义。" };
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
            ["在日线/4H 确认关注区和目标的不同角色。", "价格到区后观察 15m/5m 反应，记录支持与冲突证据。", "目标、确认与失效依据齐全后，再填写候选交易计划。"]);
    }
}
