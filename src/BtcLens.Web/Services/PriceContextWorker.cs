using BtcLens.Infrastructure;

namespace BtcLens.Web.Services;

public sealed class PriceContextWorker(MarketEngine market, ILogger<PriceContextWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (market.View.Mode == "live" && market.View.Instrument is not null)
            {
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                void Changed() { if (market.View.Mode != "live") session.Cancel(); }
                market.Changed += Changed;
                try { await market.LoadContextHistoryAsync(session.Token); }
                catch (OperationCanceledException) when (session.IsCancellationRequested) { }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
                {
                    log.LogWarning("5m history refresh failed: {Message}", ex.Message);
                    await market.SetContextStatusAsync("5m 历史加载暂时失败", "历史数据尚未补齐，当前指标可能仅覆盖部分区间；稍后重试。", stoppingToken);
                }
                finally { market.Changed -= Changed; }
            }
            await Task.Delay(market.View.Mode == "live" && market.View.Instrument is not null ? 60_000 : 1000, stoppingToken);
        }
    }
}
