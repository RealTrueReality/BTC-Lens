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
    private bool connected;
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
    public void Begin(InstrumentInfo contract, long now)
    {
        if (contract.Id != FlowRules.Perpetual || contract.ContractCurrency != "BTC" || contract.ContractValue <= 0)
            throw new ArgumentException("Unsupported contract metadata.");
        lock (sync)
        {
            if (active.Count > 0) throw new InvalidOperationException("Previous collection segment is still open.");
            foreach (var instrument in FlowRules.Instruments)
                active[instrument] = new(Guid.NewGuid().ToString("N"), instrument, now, now,
                    "公共 trades 订阅；自首笔接收成交起累计，之前历史未回补", contract);
            connected = true; error = null;
            FlushLocked(now);
        }
        Changed?.Invoke();
    }
    public void Accept(MarketTrade trade, long receivedAt)
    {
        lock (sync)
        {
            if (!connected || !active.TryGetValue(trade.Instrument, out var segment)) return;
            var key = trade.Instrument + ":" + trade.TradeId;
            var existing = pendingTrades.GetValueOrDefault(key) ?? store.FindCapturedTrade(key);
            if (existing is not null)
            {
                if (existing.Trade != trade) throw new InvalidDataException("同一成交 ID 的内容冲突，停止当前采集段。");
                return;
            }
            if (segment.LastTradeAt is long prior && trade.Timestamp - prior > 30_000)
                throw new InvalidDataException("两笔成交观测间隔超过 30 秒，无法确认连续性。");
            var minuteId = segment.Id + ":" + trade.Timestamp / 60_000 * 60_000;
            var previous = pendingPoints.GetValueOrDefault(minuteId) ?? view.Points.LastOrDefault(p => p.Id == minuteId);
            var update = FlowRules.Apply(segment, previous, trade, segment.Contract, receivedAt);
            active[trade.Instrument] = update.Segment;
            pendingPoints[update.Point.Id] = update.Point;
            pendingTrades[key] = update.Trade;
        }
    }
    public void Flush(long now)
    {
        lock (sync)
        {
            if (!connected && pendingTrades.Count == 0) return;
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
        view = Load(now, connected ? "WebSocket 主动成交 · 每秒保存" : "采集暂停 · 历史段保留");
    }
    public void End(string reason, long now)
    {
        lock (sync)
        {
            foreach (var (key, segment) in active.ToArray())
                active[key] = segment with { EndedAt = now, EndReason = reason };
            connected = false; error = reason;
            FlushLocked(now); active.Clear();
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
    private FlowView Load(long now, string status) => new("live", status, error, store.FlowSegments(),
        store.CvdPoints(now - 86_400_000), store.OpenInterest(now - 86_400_000), now) { OiError = oiError };
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
