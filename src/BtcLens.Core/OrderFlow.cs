namespace BtcLens.Core;

public static class FlowRules
{
    public const string Version = "Observed-Taker-CVD/1.0";
    public const string Spot = "BTC-USDT", Perpetual = "BTC-USDT-SWAP";
    public const string Okx = "OKX", Binance = "Binance";
    public static readonly string[] Exchanges = [Okx, Binance];
    public static readonly string[] Instruments = [Spot, Perpetual];
    public static decimal QuantityBtc(MarketTrade trade, InstrumentInfo contract)
    {
        if (!Exchanges.Contains(trade.Exchange) || !Instruments.Contains(trade.Instrument) || trade.Price <= 0 || trade.Size <= 0 ||
            trade.Side is not ("buy" or "sell") || trade.TradeId <= 0 || trade.Count < 1 || trade.Count > trade.TradeId)
            throw new ArgumentException("Invalid public trade.");
        if (trade.Instrument == Spot || trade.Exchange == Binance) return trade.Size;
        if (contract.Id != Perpetual || contract.ContractCurrency != "BTC" || contract.ContractValue <= 0)
            throw new ArgumentException("BTC-valued linear contract metadata is required.");
        // sz is the aggregate quantity. count is audit metadata, never a volume multiplier.
        return trade.Size * contract.ContractValue;
    }
    public static FlowUpdate Apply(FlowSegment segment, CvdPoint? previous, MarketTrade trade,
        InstrumentInfo contract, long receivedAt)
    {
        var qty = QuantityBtc(trade, contract);
        if (segment.EndedAt is not null || segment.Instrument != trade.Instrument || segment.Exchange != trade.Exchange)
            throw new ArgumentException("Trade does not belong to an open segment.");
        if (trade.Timestamp > receivedAt + 5_000 || receivedAt - trade.Timestamp > 30_000)
            throw new InvalidDataException("成交时间过期或位于未来，当前段停止累计。");
        if (segment.LastTradeId is long last && (trade.TradeId - trade.Count + 1 <= last || trade.Timestamp < segment.LastTradeAt))
            throw new InvalidDataException("成交乱序或聚合范围重叠，需要重新分段核对。");
        var buy = trade.Side == "buy" ? qty : 0;
        var sell = trade.Side == "sell" ? qty : 0;
        var notional = qty * trade.Price;
        var next = segment with { StartedAt = segment.Messages == 0 ? trade.Timestamp : segment.StartedAt,
            LastTradeAt = trade.Timestamp, LastTradeId = trade.TradeId, Messages = segment.Messages + 1,
            LastAggregateId = trade.AggregateId,
            Matches = segment.Matches + trade.Count, BuyBtc = segment.BuyBtc + buy, SellBtc = segment.SellBtc + sell,
            BuyUsdt = (segment.BuyUsdt ?? 0) + (buy > 0 ? notional : 0), SellUsdt = (segment.SellUsdt ?? 0) + (sell > 0 ? notional : 0) };
        // Bucket timestamp is the LAST observed event, never the minute open with later evidence.
        var minute = trade.Timestamp / 60_000 * 60_000;
        var same = previous is not null && previous.Minute == minute && previous.SegmentId == segment.Id;
        var point = new CvdPoint(segment.Id + ":" + minute, segment.Id, trade.Instrument, minute,
            trade.Timestamp, next.CvdBtc, (same ? previous!.BuyBtc : 0) + buy, (same ? previous!.SellBtc : 0) + sell,
            (same ? previous!.Messages : 0) + 1) { Exchange = trade.Exchange, CvdUsdt = next.BuyUsdt - next.SellUsdt,
            BuyUsdt = (same ? previous!.BuyUsdt ?? 0 : 0) + (buy > 0 ? notional : 0), SellUsdt = (same ? previous!.SellUsdt ?? 0 : 0) + (sell > 0 ? notional : 0) };
        return new(next, point, new CapturedTrade(TradeKey(trade),
            segment.Id, receivedAt, qty, trade));
    }
    public static string TradeKey(MarketTrade trade) => (trade.Exchange == Okx ? "" : trade.Exchange + ":") + trade.Instrument + ":" + trade.TradeId;
    public static string MarketKey(string exchange, string instrument) => exchange + ":" + instrument;
}

public sealed record MarketTrade(string Instrument, long TradeId, decimal Price, decimal Size, string Side,
    long Timestamp, int Count, string Source, long? SequenceId)
{
    public string Exchange { get; init; } = FlowRules.Okx;
    public long? AggregateId { get; init; }
    public string? RawJson { get; init; }
}
public sealed record CapturedTrade(string Id, string SegmentId, long ReceivedAt, decimal QuantityBtc, MarketTrade Trade);
public sealed record FlowSegment(string Id, string Instrument, long ConnectedAt, long StartedAt,
    string StartReason, InstrumentInfo Contract, long? EndedAt = null, string? EndReason = null,
    long? LastTradeAt = null, long? LastTradeId = null, long Messages = 0, long Matches = 0,
    decimal BuyBtc = 0, decimal SellBtc = 0)
{
    public string Exchange { get; init; } = FlowRules.Okx;
    public decimal? BuyUsdt { get; init; }
    public decimal? SellUsdt { get; init; }
    public long? LastAggregateId { get; init; }
    public decimal CvdBtc => BuyBtc - SellBtc;
    public string Algorithm => Exchange == FlowRules.Okx ? FlowRules.Version : "Observed-Taker-CVD/1.1:Binance-BTC";
}
public sealed record CvdPoint(string Id, string SegmentId, string Instrument, long Minute, long Timestamp,
    decimal CvdBtc, decimal BuyBtc, decimal SellBtc, long Messages)
{
    public string Exchange { get; init; } = FlowRules.Okx;
    public decimal? CvdUsdt { get; init; }
    public decimal? BuyUsdt { get; init; }
    public decimal? SellUsdt { get; init; }
}
public sealed record FlowUpdate(FlowSegment Segment, CvdPoint Point, CapturedTrade Trade);
public sealed record FlowCapture(FlowSegment Segment, CapturedTrade[] Trades);
public sealed record OpenInterestSample(string Instrument, long Timestamp, decimal Contracts,
    decimal CoinBtc, decimal Usd, long ReceivedAt, string Transport)
{
    public string Id => Instrument + ":" + Timestamp;
    public bool Valid => Instrument == FlowRules.Perpetual && Contracts >= 0 && CoinBtc >= 0 && Usd >= 0;
}
public sealed record FlowView(string Mode, string Status, string? Error, FlowSegment[] Segments,
    CvdPoint[] Points, OpenInterestSample[] OpenInterest, long AsOf)
{
    public static FlowView Empty => new("live", "尚未采集", null, [], [], [], 0);
    public string? OiError { get; init; }
    public IReadOnlyDictionary<string,string> SourceErrors { get; init; } = new Dictionary<string,string>();
    public FlowSegment? Latest(string instrument, string exchange = FlowRules.Okx) => Segments.Where(s => s.Instrument == instrument && s.Exchange == exchange).MaxBy(s => s.ConnectedAt);
}
