using BtcLens.Application;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

public sealed class MarketEngine
{
    private readonly ILensStore store;
    private readonly OkxClient okx;
    private readonly OrderFlowEngine? orderFlow;
    private readonly TimeProvider clock;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Candle[] fixture = DemoMarket.Generate();
    private MarketView view = MarketView.Empty;
    private readonly Dictionary<string, PriceSample> samples = new();
    private Candle[] contextCache = [];
    private string contextStatus = "等待 5m 历史", contextError = "";
    public MarketView View { get { var state = Volatile.Read(ref view); return state.Mode == "live" ? state with { Flow = orderFlow?.View ?? FlowView.Empty, Cutoff = Now } : state; } }
    public event Action? Changed;

    public MarketEngine(ILensStore store, OkxClient okx, OrderFlowEngine? orderFlow = null, TimeProvider? timeProvider = null)
    {
        this.store = store; this.okx = okx; this.orderFlow = orderFlow; clock = timeProvider ?? TimeProvider.System;
        var earliest = clock.GetUtcNow().AddDays(-40).ToUnixTimeMilliseconds();
        contextCache = store.ReadCandles("live:analysis").Where(c => c.OpenTime >= earliest).ToArray();
        if (orderFlow is not null) orderFlow.Changed += Notify;
        var session = store.GetSetting("demo-session") ?? Guid.NewGuid().ToString("N")[..10];
        store.SetSetting("demo-session", session);
        var index = int.TryParse(store.GetSetting("demo-index"), out var saved) ? Math.Clamp(saved, DemoMarket.InitialIndex, fixture.Length) : DemoMarket.InitialIndex;
        view = BuildDemo(index, "demo:" + session);
        SeedDemoTrades();
        if (store.GetSetting("market-mode") == "live") view = LiveEmpty();
        else SeedWatch();
    }
    private MarketView BuildDemo(int index, string scope)
    {
        var candles = DemoMarket.Visible(fixture, index); var last = fixture[index - 1];
        var state = new MarketView("demo", scope, "合成数据 · 5m 收盘回放", "离线回放", null,
            new("BTC-USDT-SWAP", .1m, .01m, "BTC"), new(last.Close, last.CloseTime, "5m 收盘回放"), last.CloseTime,
            candles, Detect(candles, last.CloseTime), Indicators.Levels(candles, last.CloseTime), index, fixture.Length)
            { Flow = DemoOrderFlow.Build(fixture, index, scope), ContextCandles = fixture.Take(index).ToArray() };
        return Enrich(state);
    }
    private MarketView Enrich(MarketView state)
    {
        var offset = store.GetSetting("vwap-offset:" + state.Scope) == "0" ? 0 : 8;
        return state with { PriceContext = new(state.Mode == "demo" ? "合成 5m 收盘数据" : contextStatus,
            state.Mode == "live" && contextError.Length > 0 ? contextError : null, offset,
            state.Instrument is null ? [] : PriceContextRules.Vwaps(state.ContextCandles, state.Cutoff, state.Instrument, offset), store.Profiles(state.Scope)) };
    }
    private MarketView LiveEmpty() => new("live", "live", "正在连接 OKX", "等待行情", null, null, null,
        Now, new Dictionary<string, Candle[]>(), [], [], 0, 0);
    private static DetectedZone[] Detect(Dictionary<string, Candle[]> candles, long cutoff) =>
        candles.Where(x => Timeframes.All.Contains(x.Key)).SelectMany(x => Indicators.Detect(x.Value, cutoff)).ToArray();

    public async Task SwitchModeAsync(string mode)
    {
        if (mode is not ("live" or "demo")) throw new ArgumentException("Invalid mode.");
        await gate.WaitAsync();
        try
        {
            if (mode == View.Mode) return;
            store.SetSetting("market-mode", mode);
            if (mode == "live") view = LiveEmpty();
            else view = BuildDemo(int.Parse(store.GetSetting("demo-index") ?? DemoMarket.InitialIndex.ToString()), "demo:" + store.GetSetting("demo-session"));
            foreach (var w in store.Watches(view.Scope)) store.SaveWatch(w with { LastSample = null });
            samples.Clear();
        }
        finally { gate.Release(); }
        Notify();
    }
    public async Task StepAsync(int count = 1)
    {
        if (count is < 1 or > 10) throw new ArgumentException("每次回放 1–10 根。");
        await gate.WaitAsync();
        try
        {
            if (view.Mode != "demo") return;
            for (var i = 0; i < count && view.ReplayIndex < fixture.Length; i++)
            {
                view = BuildDemo(view.ReplayIndex + 1, view.Scope);
                ProcessQuote(view.Quote!, true);
            }
            store.SetSetting("demo-index", view.ReplayIndex.ToString());
        }
        finally { gate.Release(); }
        Notify();
    }
    public async Task NewReplayAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (view.Mode != "demo") return;
            var session = Guid.NewGuid().ToString("N")[..10];
            store.SetSetting("demo-session", session); store.SetSetting("demo-index", DemoMarket.InitialIndex.ToString());
            view = BuildDemo(DemoMarket.InitialIndex, "demo:" + session); SeedWatch();
            samples.Clear();
        }
        finally { gate.Release(); }
        Notify();
    }
    public async Task RefreshLiveAsync(CancellationToken ct)
    {
        if (View.Mode != "live") return;
        var tasks = Timeframes.All.Append("1W").Select(async tf => (tf, bars: await okx.CandlesAsync(tf, ct))).ToArray();
        var instrumentTask = okx.InstrumentAsync(ct);
        await Task.WhenAll(tasks.Cast<Task>().Append(instrumentTask));
        var result = tasks.Select(t => t.Result).ToArray(); var instrument = instrumentTask.Result;
        var now = Now;
        var candles = result.ToDictionary(x => x.tf, x => x.bars);
        await gate.WaitAsync(ct);
        try
        {
            if (view.Mode != "live") return;
            store.SaveCandles("live", candles.Values.SelectMany(x => x));
            MergeContext(candles["5m"], now);
            view = Enrich(view with { Instrument = instrument, Candles = candles, ContextCandles = contextCache, Zones = Detect(candles, now), Levels = Indicators.Levels(candles, now), Cutoff = now, Error = null,
                Status = view.Quote is not null && now - view.Quote.Timestamp <= 30_000 ? "OKX 公共行情" : "等待新鲜价格" });
        }
        finally { gate.Release(); }
        Notify();
    }
    public async Task AcceptLiveQuoteAsync(PriceSample quote, CancellationToken ct)
    {
        var now = Now;
        if (quote.Price <= 0 || now - quote.Timestamp > 30_000 || quote.Timestamp > now + 5000) return;
        await gate.WaitAsync(ct);
        try
        {
            if (view.Mode != "live" || view.Quote is not null && quote.Timestamp <= view.Quote.Timestamp) return;
            var continuity = view.Quote is not null && quote.Timestamp - view.Quote.Timestamp <= 30_000;
            view = view with { Quote = quote, Transport = quote.Transport, Cutoff = now,
                Status = view.Candles.Count > 0 ? "OKX 公共行情" : "价格已连接 · K 线加载中" };
            ProcessQuote(quote, continuity);
        }
        finally { gate.Release(); }
        Notify();
    }
    private void MergeContext(IEnumerable<Candle> incoming, long now)
    {
        var closed = incoming.Where(c => c.Valid && c.Confirmed && c.Timeframe == "5m" && c.CloseTime <= now).ToArray();
        store.SaveCandles("live:analysis", closed);
        contextCache = contextCache.Concat(closed).GroupBy(c => c.OpenTime).Select(g => g.Last())
            .Where(c => c.OpenTime >= now - 40 * 86_400_000L).OrderBy(c => c.OpenTime).ToArray();
    }
    public async Task LoadContextHistoryAsync(CancellationToken ct)
    {
        var state = View;
        if (state.Mode != "live" || state.Instrument is null) return;
        var now = Now;
        var start = new[] { 0, 8 }.SelectMany(offset => new[] { "week", "month" }.Select(p => PriceContextRules.Anchor(now, p, offset))).Min();
        var end = now / PriceContextRules.Step * PriceContextRules.Step;
        var existing = state.ContextCandles.Select(c => c.OpenTime).ToHashSet();
        var missing = Enumerable.Range(0, (int)((end - start) / PriceContextRules.Step)).Select(i => start + i * PriceContextRules.Step)
            .Where(t => !existing.Contains(t)).ToArray();
        if (missing.Length == 0) { await SetContextStatusAsync("5m 历史已覆盖日/周/月锚点", null, ct); return; }
        var cursor = missing[^1] + PriceContextRules.Step;
        await SetContextStatusAsync("正在补充 5m 历史 · 已加载 " + state.ContextCandles.Length + " 根", null, ct);
        for (var page = 0; page < 40 && cursor > missing[0]; page++)
        {
            ct.ThrowIfCancellationRequested();
            var bars = await okx.HistoryCandlesAsync(cursor, ct);
            var older = bars.Where(c => c.OpenTime < cursor).ToArray();
            if (older.Length == 0) throw new InvalidDataException("历史分页没有向前推进。");
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (view.Mode != "live") return;
                MergeContext(older.Where(c => c.OpenTime >= start), Now);
                contextStatus = "正在补充 5m 历史 · 已加载 " + contextCache.Length + " 根"; contextError = "";
                view = Enrich(view with { ContextCandles = contextCache, Cutoff = Now });
            }
            finally { gate.Release(); }
            Notify(); cursor = older.Min(c => c.OpenTime);
            await Task.Delay(200, ct);
        }
        await SetContextStatusAsync("5m 历史加载结束 · " + contextCache.Length + " 根，完整性见各周期覆盖", null, ct);
    }
    public async Task SetContextStatusAsync(string status, string? error, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (view.Mode != "live") return;
            contextStatus = status; contextError = error ?? "";
            view = Enrich(view with { ContextCandles = contextCache, Cutoff = Now });
        }
        finally { gate.Release(); }
        Notify();
    }
    public async Task SetVwapOffsetAsync(int offset)
    {
        if (offset is not (0 or 8)) throw new ArgumentException("Invalid VWAP timezone.");
        await gate.WaitAsync();
        try { store.SetSetting("vwap-offset:" + view.Scope, offset.ToString()); view = Enrich(view with { Cutoff = View.Cutoff }); }
        finally { gate.Release(); }
        Notify();
    }
    public async Task<SavedProfile> SaveProfileAsync(string name, string timeframe, long start, long end, int rows, decimal area, string expectedScope)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || !Timeframes.All.Contains(timeframe)) throw new ArgumentException("填写选区名称和有效周期。");
        await gate.WaitAsync();
        SavedProfile saved;
        try
        {
            if (view.Scope != expectedScope) throw new ArgumentException("工作区已变化，请重新选择范围。");
            var state = View;
            var result = PriceContextRules.Profile(state.ContextCandles, start, end, state.Cutoff,
                state.Instrument ?? throw new ArgumentException("等待合约元数据。"), rows, area);
            if (result.Poc is null) throw new ArgumentException("所选范围没有可用的已收盘成交量，请检查数据覆盖。");
            saved = new(Guid.NewGuid().ToString("N"), state.Scope, name.Trim(), timeframe, state.Cutoff, result,
                PriceContextRules.ClosedBars(state.ContextCandles, start, end, state.Cutoff));
            store.SaveProfile(saved);
            view = Enrich(view with { Cutoff = state.Cutoff });
        }
        finally { gate.Release(); }
        Notify(); return saved;
    }
    public async Task ReportErrorAsync(string message)
    {
        await gate.WaitAsync();
        try
        {
            if (view.Mode != "live") return;
            var stale = view.Quote is null || Now - view.Quote.Timestamp > 30_000;
            view = view with { Error = message, Status = stale ? "数据过期 · 到区提醒暂停" : "部分数据暂不可用" };
        }
        finally { gate.Release(); }
        Notify();
    }
    public async Task<WatchZone> SaveWatchAsync(string? id, int? expectedRevision, WatchVersion draft)
    {
        WatchRules.Validate(draft);
        await gate.WaitAsync();
        WatchZone updated;
        try
        {
            var old = id is null ? null : store.Watches(view.Scope).FirstOrDefault(w => w.Id == id);
            if (id is not null && (old is null || old.Current.Revision != expectedRevision)) throw new ArgumentException("关注区已变化，请重新打开编辑。");
            var current = draft with { Revision = (old?.Current.Revision ?? 0) + 1, CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(view.Cutoff) };
            updated = new(old?.Id ?? Guid.NewGuid().ToString("N"), view.Scope, true, false, current,
                old is null ? [] : [.. old.History, old.Current], view.Quote);
            store.SaveWatch(updated);
            if (view.Quote is not null) samples[updated.Id + ":" + updated.Current.Revision] = view.Quote;
        }
        finally { gate.Release(); }
        Notify(); return updated;
    }
    public async Task ToggleWatchAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            var w = store.Watches(view.Scope).First(x => x.Id == id);
            store.SaveWatch(w with { Enabled = !w.Enabled, LastSample = view.Quote });
            if (view.Quote is not null) samples[w.Id + ":" + w.Current.Revision] = view.Quote;
        }
        finally { gate.Release(); }
        Notify();
    }
    public async Task DeleteWatchAsync(string id, int expectedRevision, string expectedScope)
    {
        await gate.WaitAsync();
        try
        {
            if (view.Scope != expectedScope) throw new ArgumentException("工作区已变化，请刷新后再删除关注区。");
            if (!store.DeleteWatch(view.Scope, id, expectedRevision, DateTimeOffset.FromUnixTimeMilliseconds(view.Cutoff)))
                throw new ArgumentException("关注区已变化或已删除，请刷新后重试。");
            foreach (var key in samples.Keys.Where(key => key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray()) samples.Remove(key);
        }
        finally { gate.Release(); }
        Notify();
    }
    private void ProcessQuote(PriceSample quote, bool continuity)
    {
        foreach (var w in store.Watches(view.Scope))
        {
            if (!w.Enabled || w.Triggered) continue;
            var key = w.Id + ":" + w.Current.Revision;
            var observed = samples.TryGetValue(key, out var prior) ? w with { LastSample = prior } : w;
            samples[key] = quote;
            var next = observed with { LastSample = quote };
            if (WatchRules.Entered(observed, quote, view.Cutoff, continuity, view.Mode == "demo" ? 300_000 : 30_000))
            {
                next = next with { Triggered = true };
                var evidence = new { mode = view.Mode, scope = view.Scope, cutoff = view.Cutoff, quote, zone = w.Current,
                    rule = WatchRules.Version, instrument = view.Instrument, sourceZone = view.Zones.FirstOrDefault(z => z.Id == w.Current.SourceZoneId),
                    candles = view.Candles.ToDictionary(x => x.Key, x => x.Value.Where(c => c.Confirmed && c.CloseTime <= view.Cutoff).TakeLast(20).ToArray()),
                    levels = view.Levels, dataStatus = view.Status, orderFlow = View.Flow, priceContext = view.PriceContext.Evidence(view.Cutoff),
                    gaps = view.Candles.Where(x => HasGap(x.Value, view.Cutoff)).Select(x => x.Key).ToArray() };
                var alert = new AlertEvent(WatchRules.EventId(w), w.Scope, w.Id, w.Current.Revision,
                    DateTimeOffset.FromUnixTimeMilliseconds(quote.Timestamp), quote.Price, quote.Transport, w.Current,
                    $"价格首次观测进入「{w.Current.Name}」；此事件记录触及，局部确认与目标仍需判断。", JsonCodec.Write(evidence));
                store.RecordAlert(next, alert);
            }
        }
    }
    public static bool HasGap(Candle[] input, long cutoff)
    {
        var bars = input.Where(c => c.Confirmed && c.CloseTime <= cutoff).OrderBy(c => c.OpenTime).ToArray();
        return bars.Zip(bars.Skip(1)).Any(x => x.First.CloseTime != x.Second.OpenTime);
    }
    private void SeedWatch()
    {
        if (store.Watches(view.Scope, includeDeleted: true).Length > 0) return;
        var v = new WatchVersion(1, "回踩观察 · 演示关注区", 67080, 67140, "5m", Bias.Bullish,
            "合成数据示例：推进约 3 根 5m K 线可观察首次到区。请按自己的判断添加或调整关注区。", null,
            DateTimeOffset.FromUnixTimeMilliseconds(view.Cutoff));
        store.SaveWatch(new(Guid.NewGuid().ToString("N"), view.Scope, true, false, v, [], view.Quote));
    }
    private void SeedDemoTrades()
    {
        if (store.GetSetting("demo-trades-seeded") == "1") return;
        var time = new DateTimeOffset(2026, 9, 19, 9, 0, 0, TimeSpan.FromHours(8));
        var examples = new[]
        {
            new Trade { Id = "demo-example-1", Mode = "demo", Side = TradeSide.Long, EntryTime = time, ExitTime = time.AddHours(1), EntryPrice = 66800, ExitPrice = 67120, QuantityBtc = .02m, InitialStop = 66680, Target = 67200, Fees = .8m, Funding = 0, Reason = "演示：回踩关注区后记录入场依据。", Review = "演示：按照原定风险执行，平仓后补充复盘。", Tags = "按计划执行" },
            new Trade { Id = "demo-example-2", Mode = "demo", Side = TradeSide.Short, EntryTime = time.AddHours(4), ExitTime = time.AddHours(5), EntryPrice = 67250, ExitPrice = 67380, QuantityBtc = .01m, InitialStop = 67380, Target = 66900, Fees = .4m, Funding = -.05m, Reason = "演示：未等待完整局部确认。", Review = "演示：提前入场，保留止损后的原始判断。", Tags = "提前入场" },
            new Trade { Id = "demo-example-3", Mode = "demo", Side = TradeSide.Long, EntryTime = time.AddDays(1), ExitTime = time.AddDays(1).AddHours(2), EntryPrice = 67000, ExitPrice = 67180, QuantityBtc = .015m, InitialStop = 66880, Target = 67300, Fees = .6m, Funding = 0, Reason = "演示：低周期回访参考区域。", Review = "演示：记录目标前平仓的理由。", Tags = "提前止盈" }
        };
        store.ImportTrades(examples); store.SetSetting("demo-trades-seeded", "1");
    }
    private void Notify() => Changed?.Invoke();
}
