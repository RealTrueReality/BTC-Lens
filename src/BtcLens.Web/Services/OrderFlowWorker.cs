using BtcLens.Infrastructure;

namespace BtcLens.Web.Services;

public sealed class OrderFlowWorker(MarketEngine market, OrderFlowEngine flow, OkxClient okx,
    IConfiguration config, ILogger<OrderFlowWorker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Collect(stoppingToken), PollOi(stoppingToken), Flush(stoppingToken));
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private async Task Collect(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (market.View.Mode != "live") { await Task.Delay(500, ct); continue; }
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void ModeChanged() { if (market.View.Mode != "live") session.Cancel(); }
            market.Changed += ModeChanged;
            var reason = "连接结束；未收到的成交不回补";
            try
            {
                var contract = await okx.InstrumentAsync(session.Token);
                // Recheck after subscribing to the mode event to close the startup race.
                if (market.View.Mode != "live") session.Cancel();
                await foreach (var message in OkxClient.TradesStreamAsync(new Uri(config["Okx:WebSocketUrl"] ?? "wss://ws.okx.com:8443/ws/v5/public"), session.Token))
                {
                    if (message.Ready) flow.Begin(contract, Now());
                    foreach (var trade in message.Trades) flow.Accept(trade, Now());
                }
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or
                OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or ArgumentException or FormatException or OverflowException)
            {
                reason = market.View.Mode != "live" ? "切换到演示；采集暂停，期间成交未回补" : "成交连接中断或校验失败；缺口未回补";
                if (!ct.IsCancellationRequested && market.View.Mode == "live")
                    log.LogWarning("Public trade stream ended: {Message}", ex.Message);
            }
            finally
            {
                market.Changed -= ModeChanged;
                if (ct.IsCancellationRequested) reason = "进程停止；停机期间成交未回补";
                flow.End(reason, Now());
            }
            if (!ct.IsCancellationRequested) await Task.Delay(3_000, ct);
        }
    }
    private async Task PollOi(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (market.View.Mode == "live")
            {
                try
                {
                    var sample = await okx.OpenInterestAsync(ct);
                    if (market.View.Mode == "live") flow.AcceptOpenInterest(sample, Now());
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
                {
                    if (ct.IsCancellationRequested) break;
                    log.LogWarning("Open interest polling failed: {Message}", ex.Message);
                    flow.ReportOiError("OI 请求失败，保留最后样本；请检查数据时间。");
                }
            }
            await Task.Delay(10_000, ct);
        }
    }
    private async Task Flush(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct)) flow.Flush(Now());
    }
}
