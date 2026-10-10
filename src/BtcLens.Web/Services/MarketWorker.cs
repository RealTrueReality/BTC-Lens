using BtcLens.Infrastructure;

namespace BtcLens.Web.Services;

public sealed class MarketWorker(MarketEngine market, OkxClient okx, IConfiguration config, ILogger<MarketWorker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(Poll(stoppingToken), Stream(stoppingToken));
    private async Task Poll(CancellationToken ct)
    {
        var refreshed = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (market.View.Mode == "live")
            {
                try
                {
                    if (market.View.Quote is null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - market.View.Quote.Timestamp > 10_000)
                        await market.AcceptLiveQuoteAsync(await okx.TickerAsync(ct), ct);
                    if (market.View.Candles.Count == 0 || DateTimeOffset.UtcNow - refreshed > TimeSpan.FromSeconds(30))
                    { await market.RefreshLiveAsync(ct); refreshed = DateTimeOffset.UtcNow; }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
                { if (ct.IsCancellationRequested) break; log.LogWarning("Public market refresh failed: {Message}", ex.Message); await market.ReportErrorAsync("行情请求暂时失败，正在重试；保留上次数据与时间。"); }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
    private async Task Stream(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (market.View.Mode != "live") { await Task.Delay(500, ct); continue; }
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void ModeChanged() { if (market.View.Mode != "live") session.Cancel(); }
            market.Changed += ModeChanged;
            try
            {
                await foreach (var quote in OkxClient.StreamAsync(new Uri(config["Okx:WebSocketUrl"] ?? "wss://ws.okx.com:8443/ws/v5/public"), session.Token))
                    await market.AcceptLiveQuoteAsync(quote, session.Token);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
            { if (!ct.IsCancellationRequested && market.View.Mode == "live") log.LogInformation("Ticker stream disconnected; REST fallback remains available: {Message}", ex.Message); }
            finally { market.Changed -= ModeChanged; }
            if (!ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
