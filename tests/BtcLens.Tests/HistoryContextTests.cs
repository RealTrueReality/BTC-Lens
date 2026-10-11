using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BtcLens.Core;
using BtcLens.Infrastructure;

namespace BtcLens.Tests;

public sealed class HistoryContextTests
{
    [Fact] public async Task HistoricalPaginationFillsBothTimezoneAnchorsAndWarmCacheSkipsBackfill()
    {
        var directory = Path.Combine(Path.GetTempPath(), "btclens-history-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "test.db");
        var clock = new FixedClock();
        var handler = new PublicHistory(clock);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        try
        {
            using (var store = new LensStore(path))
            {
                var market = new MarketEngine(store, new OkxClient(http), timeProvider: clock); await market.SwitchModeAsync("live");
                await market.RefreshLiveAsync(default);
                Assert.Contains(market.View.PriceContext.Vwaps, v => !v.Coverage.Complete);
                await market.LoadContextHistoryAsync(default);
                Assert.InRange(handler.Cursors.Count, 1, 40);
                Assert.True(handler.Cursors.Zip(handler.Cursors.Skip(1)).All(x => x.First > x.Second));
                Assert.All(market.View.PriceContext.Vwaps, v => Assert.True(v.Coverage.Complete));
                await market.SetVwapOffsetAsync(0); Assert.All(market.View.PriceContext.Vwaps, v => Assert.True(v.Coverage.Complete));
                Assert.True(store.ReadCandles("live:analysis").Length > 300);
            }
            var count = handler.Cursors.Count;
            using (var store = new LensStore(path))
            {
                var market = new MarketEngine(store, new OkxClient(http), timeProvider: clock); await market.RefreshLiveAsync(default); await market.LoadContextHistoryAsync(default);
                Assert.Equal(count, handler.Cursors.Count); Assert.All(market.View.PriceContext.Vwaps, v => Assert.True(v.Coverage.Complete));
                clock.Advance(TimeSpan.FromMinutes(5));
                await market.LoadContextHistoryAsync(default);
                Assert.Equal(count + 1, handler.Cursors.Count);
                Assert.All(market.View.PriceContext.Vwaps, v => Assert.True(v.Coverage.Complete));
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("btclens-history-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class PublicHistory(TimeProvider clock) : HttpMessageHandler
    {
        public List<long> Cursors { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString(); var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            object[] data;
            if (uri.Contains("instruments")) data = [new { instId = "BTC-USDT-SWAP", tickSz = "0.1", ctVal = "0.01", ctValCcy = "BTC" }];
            else
            {
                var tf = Regex.Match(uri, "bar=([^&]+)").Groups[1].Value;
                var step = Timeframes.Milliseconds(tf); var cursor = now + 2 * step;
                if (uri.Contains("history-candles")) { cursor = long.Parse(Regex.Match(uri, "after=(\\d+)").Groups[1].Value); Cursors.Add(cursor); }
                data = Enumerable.Range(1, 300).Select(i => (object)new[] { ((cursor / step - i) * step).ToString(), "100", "110", "90", "100", "100", "1", "100", "1" }).ToArray();
            }
            var json = System.Text.Json.JsonSerializer.Serialize(new { code = "0", data });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
