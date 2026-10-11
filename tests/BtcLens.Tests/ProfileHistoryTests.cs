using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BtcLens.Core;
using BtcLens.Infrastructure;
using SqlSugar;

namespace BtcLens.Tests;

public sealed class ProfileHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "btclens-profile-history-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "test.db");
    private static long At(string date) => DateTimeOffset.Parse(date + "T00:00:00+08:00").ToUnixTimeMilliseconds();
    private static readonly long Start = At("2026-07-01"), End = At("2026-08-21");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero); }
    private static Candle Bar(long time) => new("BTC-USDT-SWAP", "5m", time, 100, 110, 90, 100, 100, true);
    private static HttpClient Client(History handler) => new(handler) { BaseAddress = new Uri("https://example.test/") };
    private static async Task<MarketEngine> Engine(LensStore store, HttpClient http, bool fixture = false)
    { var m = new MarketEngine(store, new OkxClient(http), timeProvider: new Clock(), useFixture: fixture); await m.SwitchModeAsync("live"); await m.RefreshLiveAsync(default); return m; }
    [Fact] public async Task FiftyOneDayRangeLoadsDirectlyAndSurvivesRestartWithoutRedownloading()
    {
        var handler = new History(); using var http = Client(handler); string frozen;
        var updates = new List<ProfileLoadProgress>();
        using (var store = new LensStore(PathName))
        {
            var m = await Engine(store, http); var beforeVwap = JsonCodec.Write(m.View.PriceContext.Vwaps);
            var p = await m.SaveProfileAsync("July consolidation", "4H", Start, End, 40, 70, "live", new ProgressSink(updates));
            Assert.Equal(14688, p.SourceBars.Length); Assert.Equal(14688m, p.Result.TotalBtc); Assert.True(p.Result.Coverage.Complete);
            Assert.Equal(49, handler.Cursors.Count); Assert.Equal(End, handler.Cursors[0]);
            Assert.True(handler.Cursors.Zip(handler.Cursors.Skip(1)).All(x => x.First > x.Second));
            Assert.Equal(14688, updates[^1].Loaded); Assert.Equal(14688, updates[^1].Expected);
            Assert.Equal(beforeVwap, JsonCodec.Write(m.View.PriceContext.Vwaps));
            frozen = JsonCodec.Write(p); Assert.Equal(p.Result.SourceHash, PriceContextRules.Hash(p.SourceBars));
        }
        using (var store = new LensStore(PathName))
        {
            var m = await Engine(store, http); var result = await m.PreviewProfileAsync(Start, End, 40, 70, "live");
            Assert.True(result.Coverage.Complete); Assert.Equal(49, handler.Cursors.Count);
            Assert.Equal(frozen, JsonCodec.Write(Assert.Single(store.Profiles("live"))));
        }
    }
    [Fact] public async Task OnlyMissingRangesAreRequestedAndEmptyPagesStayExplicitlyIncomplete()
    {
        using var store = new LensStore(PathName); var end = Start + 20 * PriceContextRules.Step;
        store.SaveCandles("live:analysis", Enumerable.Range(0, 20).Where(i => i is not (2 or 15)).Select(i => Bar(Start + i * PriceContextRules.Step)));
        var handler = new History { Empty = true }; using var http = Client(handler); var m = await Engine(store, http);
        var r = await m.PreviewProfileAsync(Start, end, 40, 70, "live");
        Assert.Equal(new[] { Start + 16 * PriceContextRules.Step, Start + 3 * PriceContextRules.Step }, handler.Cursors);
        Assert.False(r.Coverage.Complete); Assert.Equal(2, r.Coverage.Missing);
        await Assert.ThrowsAsync<ArgumentException>(() => m.SaveProfileAsync("incomplete", "5m", Start, end, 40, 70, "live"));
        Assert.Empty(store.Profiles("live"));
        var p = await m.SaveProfileAsync("explicit partial", "5m", Start, end, 40, 70, "live", allowIncomplete: true);
        Assert.False(p.Result.Coverage.Complete); Assert.Equal(18, p.SourceBars.Length);
        handler.Empty = false; var complete = await m.PreviewProfileAsync(Start, end, 40, 70, "live");
        Assert.True(complete.Coverage.Complete); Assert.Equal(2, store.Profiles("live")[0].Result.Coverage.Missing);
    }
    [Fact] public async Task CancellationKeepsCompletedPagesAndRetrySkipsThemWithoutCreatingAProfile()
    {
        using var store = new LensStore(PathName); using var cancel = new CancellationTokenSource();
        var handler = new History { OnHistory = n => { if (n == 2) cancel.Cancel(); return Task.CompletedTask; } };
        using var http = Client(handler); var m = await Engine(store, http); var end = Start + 900 * PriceContextRules.Step;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.SaveProfileAsync("cancel", "5m", Start, end, 40, 70, "live", ct: cancel.Token));
        Assert.Empty(store.Profiles("live")); Assert.Equal(300, store.ReadCandles("live:analysis", Start, end).Length);
        handler.OnHistory = null; var before = handler.Cursors.Count;
        var p = await m.SaveProfileAsync("retry", "5m", Start, end, 40, 70, "live");
        Assert.True(p.Result.Coverage.Complete); Assert.Equal(end - 300 * PriceContextRules.Step, handler.Cursors[before]);
    }
    [Fact] public async Task SlowHistoryDoesNotBlockQuotesAndCancelledWorkCannotSave()
    {
        using var store = new LensStore(PathName); using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new History { OnHistory = async _ => { started.TrySetResult(); await release.Task; } };
        using var http = Client(handler); var m = await Engine(store, http);
        var loading = m.SaveProfileAsync("blocked", "5m", Start, Start + PriceContextRules.Step, 40, 70, "live", ct: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await m.AcceptLiveQuoteAsync(new(125, m.View.Cutoff, "test"), default).WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(125, m.View.Quote!.Price);
        cancel.Cancel(); release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading); Assert.Empty(store.Profiles("live"));
    }
    [Fact] public async Task ScopeChangeAndNonAdvancingPagesAreRejected()
    {
        using var store = new LensStore(PathName); var handler = new History(); using var http = Client(handler); var m = await Engine(store, http, true);
        handler.OnHistory = _ => m.SwitchModeAsync("demo");
        await Assert.ThrowsAsync<ArgumentException>(() => m.SaveProfileAsync("wrong scope", "5m", Start, Start + PriceContextRules.Step, 40, 70, "live"));
        Assert.Empty(store.Profiles("live"));
        await m.SwitchModeAsync("live"); await m.RefreshLiveAsync(default); handler.OnHistory = null; handler.NonAdvancing = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => m.PreviewProfileAsync(Start - PriceContextRules.Step, Start, 40, 70, "live"));
    }
    [Fact] public async Task InvalidFutureAndMisalignedRangesNeverMakeRequests()
    {
        using var store = new LensStore(PathName); var handler = new History(); using var http = Client(handler); var m = await Engine(store, http);
        await Assert.ThrowsAsync<ArgumentException>(() => m.PreviewProfileAsync(Start + 1, End, 40, 70, "live"));
        await Assert.ThrowsAsync<ArgumentException>(() => m.PreviewProfileAsync(End, Start, 40, 70, "live"));
        await Assert.ThrowsAsync<ArgumentException>(() => m.PreviewProfileAsync(Start, m.View.Cutoff + PriceContextRules.Step, 40, 70, "live"));
        Assert.Empty(handler.Cursors);
    }
    [Fact] public void LegacyCacheGetsAnIndexWithoutChangingItsOriginalJson()
    {
        Directory.CreateDirectory(directory); var json = JsonCodec.Write(Bar(Start));
        using (var db = new SqlSugarClient(new ConnectionConfig { ConnectionString = $"Data Source={PathName};Pooling=False", DbType = DbType.Sqlite, IsAutoCloseConnection = true }))
        {
            db.Ado.ExecuteCommand("CREATE TABLE candle_cache (Id TEXT PRIMARY KEY, Scope TEXT NOT NULL, Json TEXT NOT NULL)");
            db.Ado.ExecuteCommand("INSERT INTO candle_cache VALUES ('legacy','live:analysis',@json)", new SugarParameter("@json", json));
        }
        using var store = new LensStore(PathName);
        Assert.Equal(json, JsonCodec.Write(Assert.Single(store.ReadCandles("live:analysis", Start, Start + PriceContextRules.Step))));
        Assert.Empty(store.ReadCandles("demo", Start, Start + PriceContextRules.Step));
        Assert.Empty(store.ReadCandles("live:analysis", Start + PriceContextRules.Step, End));
    }
    private sealed class ProgressSink(List<ProfileLoadProgress> updates) : IProgress<ProfileLoadProgress> { public void Report(ProfileLoadProgress value) => updates.Add(value); }
    [Fact] public void HistoricalChartBarsConserveOhlcvAndDoNotFillMissingOrFutureIntervals()
    {
        var source = Enumerable.Range(0, 288).Select(i => Bar(Start + i * PriceContextRules.Step)).ToArray();
        source[0] = source[0] with { Open = 102 }; source[^1] = source[^1] with { Close = 105 };
        var daily = Assert.Single(PriceContextRules.DisplayBars(source, "1D", Start + 86_400_000));
        Assert.Equal(Start, daily.OpenTime); Assert.Equal(102, daily.Open); Assert.Equal(105, daily.Close);
        Assert.Equal(110, daily.High); Assert.Equal(90, daily.Low); Assert.Equal(28800, daily.Volume);
        Assert.Equal(6, PriceContextRules.DisplayBars(source, "4H", Start + 86_400_000).Length);
        Assert.Empty(PriceContextRules.DisplayBars(source.Skip(1), "1D", Start + 86_400_000));
        Assert.Empty(PriceContextRules.DisplayBars(source, "1D", Start + 86_400_000 - 1));
    }
    private sealed class History : HttpMessageHandler
    {
        public List<long> Cursors { get; } = [];
        public bool Empty { get; set; }
        public bool NonAdvancing { get; set; }
        public Func<int, Task>? OnHistory { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString(); object[] data;
            if (uri.Contains("instruments")) data = [new { instId = "BTC-USDT-SWAP", tickSz = "0.1", ctVal = "0.01", ctValCcy = "BTC" }];
            else
            {
                var tf = Regex.Match(uri, "bar=([^&]+)").Groups[1].Value; var step = Timeframes.Milliseconds(tf);
                var cursor = new Clock().GetUtcNow().ToUnixTimeMilliseconds();
                if (uri.Contains("history-candles"))
                {
                    cursor = long.Parse(Regex.Match(uri, "after=(\\d+)").Groups[1].Value); Cursors.Add(cursor);
                    if (OnHistory is not null) await OnHistory(Cursors.Count); ct.ThrowIfCancellationRequested();
                }
                data = uri.Contains("history-candles") && Empty ? [] : Enumerable.Range(1, NonAdvancing ? 1 : 300)
                    .Select(i => (object)new[] { ((cursor / step - (NonAdvancing ? 0 : i)) * step).ToString(), "100", "110", "90", "100", "100", "1", "100", "1" }).ToArray();
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = "0", data }), Encoding.UTF8, "application/json") };
        }
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("btclens-profile-history-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
