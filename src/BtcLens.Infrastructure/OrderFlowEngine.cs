using BtcLens.Application;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

public sealed class OrderFlowEngine
{
    private readonly ILensStore store;
    private readonly object sync = new();
    private readonly Dictionary<string, FlowSegment> active = new();
    private readonly Dictionary<string, CvdPoint> pendingPoints = new();
    private readonly Dictionary<string, CapturedTrade> pendingTrades = new();
    private FlowView view;
    private readonly HashSet<string> connected = [];
    private readonly Dictionary<string,string> sourceErrors = [];
    private string? error;
    private string? oiError;
    private long lastFlush;
    public event Action? Changed;
    public FlowView View => Volatile.Read(ref view);

    public OrderFlowEngine(ILensStore store)
    {
        this.store = store;
        var now = Now();
        var interrupted = store.FlowSegments(100).Where(s => s.EndedAt is null)
            .Select(s => s with { EndedAt = s.LastTradeAt ?? s.ConnectedAt, EndReason = "进程重启；停机期间成交未回补" }).ToArray();
        if (interrupted.Length > 0) store.SaveFlowBatch(interrupted, [], []);
        view = Load(now, "未连接 · 以前的采集记录已保留");
    }
    public void Begin(InstrumentInfo contract, long now, string exchange = FlowRules.Okx, string[]? instruments = null)
    {
        if (contract.Id != FlowRules.Perpetual || contract.ContractCurrency != "BTC" || contract.ContractValue <= 0)
            throw new ArgumentException("Unsupported contract metadata.");
        lock (sync)
        {
            if (!FlowRules.Exchanges.Contains(exchange)) throw new ArgumentException("Unsupported exchange.");
            var markets = instruments ?? FlowRules.Instruments;
            if(markets.Length == 0 || markets.Distinct().Count() != markets.Length || markets.Any(i => !FlowRules.Instruments.Contains(i) || active.ContainsKey(FlowRules.MarketKey(exchange,i))))
                throw new InvalidOperationException("Previous collection segment is still open or market is invalid.");
            foreach (var instrument in markets)
            {
                var key = FlowRules.MarketKey(exchange, instrument);
                active[key] = new(Guid.NewGuid().ToString("N"), instrument, now, now,
                    "公共主动成交；自首笔接收成交起累计，之前历史未回补", contract) { Exchange = exchange, BuyUsdt = 0, SellUsdt = 0 };
                connected.Add(key);
                sourceErrors.Remove(key);
            }
            error = null;
            FlushLocked(now);
        }
        Changed?.Invoke();
    }
    public void Accept(MarketTrade trade, long receivedAt)
    {
        lock (sync)
        {
            var marketKey = FlowRules.MarketKey(trade.Exchange, trade.Instrument);
            if (!connected.Contains(marketKey) || !active.TryGetValue(marketKey, out var segment)) return;
            var key = FlowRules.TradeKey(trade);
            var existing = pendingTrades.GetValueOrDefault(key) ?? store.FindCapturedTrade(key);
            if (existing is not null)
            {
                if (existing.Trade != trade) throw new InvalidDataException("同一成交 ID 的内容冲突，停止当前采集段。");
                return;
            }
            if (segment.LastTradeAt is long prior && trade.Timestamp - prior > 30_000)
                throw new InvalidDataException("两笔成交观测间隔超过 30 秒，无法确认连续性。");
            if(trade.Exchange==FlowRules.Binance && segment.LastAggregateId is long aggregate && trade.AggregateId!=aggregate+1)
                throw new InvalidDataException("Binance 聚合成交 ID 不连续；需要重新分段。");
            var minuteId = segment.Id + ":" + trade.Timestamp / 60_000 * 60_000;
            var previous = pendingPoints.GetValueOrDefault(minuteId) ?? view.Points.LastOrDefault(p => p.Id == minuteId);
            var update = FlowRules.Apply(segment, previous, trade, segment.Contract, receivedAt);
            active[marketKey] = update.Segment;
            pendingPoints[update.Point.Id] = update.Point;
            pendingTrades[key] = update.Trade;
        }
    }
    public void Flush(long now)
    {
        lock (sync)
        {
            if (connected.Count == 0 && pendingTrades.Count == 0) return;
            if (now - lastFlush < 900) return;
            FlushLocked(now);
        }
        Changed?.Invoke();
    }
    private void FlushLocked(long now)
    {
        // Publish only after raw messages, aggregates and segment checkpoints commit together.
        store.SaveFlowBatch(active.Values.ToArray(), pendingPoints.Values.ToArray(), pendingTrades.Values.ToArray());
        pendingPoints.Clear(); pendingTrades.Clear(); lastFlush = now;
        view = Load(now, connected.Count > 0 ? "WebSocket 主动成交 · 每秒保存" : "采集暂停 · 历史段保留");
    }
    public void End(string reason, long now, string exchange = FlowRules.Okx, string[]? instruments = null)
    {
        lock (sync)
        {
            var affected = active.Where(kv => kv.Value.Exchange == exchange && (instruments is null || instruments.Contains(kv.Value.Instrument))).ToArray();
            foreach (var (key, segment) in affected)
            {
                active[key] = segment with { EndedAt = now, EndReason = reason };
                connected.Remove(key);
                sourceErrors[key] = reason;
            }
            if(exchange==FlowRules.Okx)error = reason;
            FlushLocked(now); foreach(var (key, _) in affected) active.Remove(key);
        }
        Changed?.Invoke();
    }
    public void AcceptOpenInterest(OpenInterestSample sample, long now)
    {
        if (!sample.Valid || now - sample.Timestamp > 30_000 || sample.Timestamp > now + 5_000)
            throw new InvalidDataException("OI 样本过期、位于未来或单位字段无效。");
        lock (sync)
        {
            store.SaveOpenInterest(sample);
            var samples = view.OpenInterest.Where(x => x.Timestamp >= now - 86_400_000 && x.Id != sample.Id)
                .Append(sample).OrderBy(x => x.Timestamp).TakeLast(3000).ToArray();
            oiError = null;
            view = view with { OpenInterest = samples, AsOf = now, OiError = null };
        }
        Changed?.Invoke();
    }
    public void ReportOiError(string message)
    {
        lock (sync) { oiError = message; view = view with { OiError = message, AsOf = Now() }; }
        Changed?.Invoke();
    }
    public void ReportSourceError(string exchange,string instrument,string message)
    {
        lock(sync){sourceErrors[FlowRules.MarketKey(exchange,instrument)]=message;view=view with {SourceErrors=new Dictionary<string,string>(sourceErrors)};}
        Changed?.Invoke();
    }
    private FlowView Load(long now, string status) => new("live", status, error, store.FlowSegments(100),
        store.CvdPoints(now - 86_400_000,10000), store.OpenInterest(now - 86_400_000), now) { OiError = oiError,SourceErrors=new Dictionary<string,string>(sourceErrors) };
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
