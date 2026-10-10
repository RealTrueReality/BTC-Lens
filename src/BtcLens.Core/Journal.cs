namespace BtcLens.Core;

public enum TradeSide { Long, Short }
public sealed record Trade
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Mode { get; init; } = "demo";
    public string Instrument { get; init; } = "BTC-USDT-SWAP";
    public TradeSide Side { get; init; }
    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset? ExitTime { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal? ExitPrice { get; init; }
    public decimal QuantityBtc { get; init; }
    public decimal? InitialStop { get; init; }
    public decimal? Target { get; init; }
    public decimal? Fees { get; init; }
    public decimal? Funding { get; init; }
    public string Reason { get; init; } = "";
    public string Review { get; init; } = "";
    public string Tags { get; init; } = "";
    public string? AlertId { get; init; }
    public string? ScreenshotDataUrl { get; init; }
    public string? ImportKey { get; init; }
    public string? EntrySnapshotJson { get; init; }
    public bool Closed => ExitTime is not null && ExitPrice is not null;
}
public sealed record TradeResult(decimal? Gross, decimal? Net, decimal? InitialRisk, decimal? NetR);
public sealed record JournalStats(int Total, int Closed, int CostComplete, int Wins, int Losses, int Breakeven,
    decimal NetPnl, decimal? WinRate, decimal? AverageWin, decimal? AverageLoss, decimal? AverageR);

public static class TradeMath
{
    public static void Validate(Trade t)
    {
        if (t.Instrument != "BTC-USDT-SWAP") throw new ArgumentException("首版仅支持 BTC-USDT-SWAP。");
        if (!Enum.IsDefined(t.Side)) throw new ArgumentException("交易方向无效。");
        if (t.Mode is not ("live" or "demo")) throw new ArgumentException("数据模式无效。");
        if (t.EntryTime == default || t.EntryPrice <= 0 || t.QuantityBtc <= 0) throw new ArgumentException("请填写有效的开仓时间、价格和 BTC 数量。");
        if ((t.ExitTime is null) != (t.ExitPrice is null)) throw new ArgumentException("平仓时间与价格必须同时填写。");
        if (t.ExitPrice is <= 0 || t.ExitTime < t.EntryTime) throw new ArgumentException("平仓价格或时间无效。");
        if (t.Fees < 0) throw new ArgumentException("手续费不能为负数。");
        if (t.InitialStop is decimal stop && (stop <= 0 || (t.Side == TradeSide.Long ? stop >= t.EntryPrice : stop <= t.EntryPrice)))
            throw new ArgumentException("初始止损必须位于开仓价的风险方向。");
        if (t.Target is decimal target && (target <= 0 || (t.Side == TradeSide.Long ? target <= t.EntryPrice : target >= t.EntryPrice)))
            throw new ArgumentException("原定目标必须位于开仓价的盈利方向。");
        if (t.Reason.Length > 4000 || t.Review.Length > 12000 || t.Tags.Length > 1000) throw new ArgumentException("记录内容过长。");
        if (t.ScreenshotDataUrl is string shot && (shot.Length > 3_000_000 ||
            !(shot.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || shot.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal))))
            throw new ArgumentException("截图仅接受 PNG/JPEG，文件最大 2 MB。");
    }
    public static TradeResult Calculate(Trade t)
    {
        Validate(t);
        decimal? risk = t.InitialStop is decimal stop ? Math.Abs(t.EntryPrice - stop) * t.QuantityBtc : null;
        decimal? gross = t.Closed ? (t.Side == TradeSide.Long ? 1 : -1) * (t.ExitPrice!.Value - t.EntryPrice) * t.QuantityBtc : null;
        decimal? net = gross.HasValue && t.Fees.HasValue && t.Funding.HasValue ? gross - t.Fees + t.Funding : null;
        return new(gross, net, risk, risk > 0 && net.HasValue ? net / risk : null);
    }
    public static JournalStats Summarize(IEnumerable<Trade> source)
    {
        var trades = source.ToArray(); var closed = trades.Where(t => t.Closed).ToArray();
        var results = closed.Select(Calculate).ToArray();
        var net = results.Where(r => r.Net.HasValue).Select(r => r.Net!.Value).ToArray();
        var wins = net.Where(n => n > 0).ToArray(); var losses = net.Where(n => n < 0).ToArray();
        var rs = results.Where(r => r.NetR.HasValue).Select(r => r.NetR!.Value).ToArray();
        return new(trades.Length, closed.Length, net.Length, wins.Length, losses.Length, net.Count(n => n == 0),
            net.Sum(), net.Length > 0 ? (decimal)wins.Length / net.Length : null,
            wins.Length > 0 ? wins.Average() : null, losses.Length > 0 ? losses.Average() : null, rs.Length > 0 ? rs.Average() : null);
    }
}
