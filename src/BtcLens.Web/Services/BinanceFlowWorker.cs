using BtcLens.Core;
using BtcLens.Infrastructure;
namespace BtcLens.Web.Services;

public sealed class BinanceFlowWorker(MarketEngine market,OrderFlowEngine flow,IConfiguration configuration,ILogger<BinanceFlowWorker> log):BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)=>Task.WhenAll(Collect(FlowRules.Spot,ct),Collect(FlowRules.Perpetual,ct));
    private static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private async Task Collect(string instrument,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            if(market.View.Mode!="live"){await Task.Delay(500,ct);continue;}
            using var session=CancellationTokenSource.CreateLinkedTokenSource(ct);
            void ModeChanged(){if(market.View.Mode!="live")session.Cancel();}
            market.Changed+=ModeChanged;var started=false;var reason="公共成交连接结束；缺失历史未回补";
            try
            {
                if(market.View.Mode!="live")session.Cancel();
                await foreach(var trade in BinancePublicTrades.Stream(instrument,configuration["Binance:ProxyUrl"],session.Token))
                {
                    if(!started){flow.Begin(new(FlowRules.Perpetual,.1m,1,"BTC"),Now(),FlowRules.Binance,[instrument]);started=true;}
                    flow.Accept(trade,Now());
                }
            }
            catch(Exception ex) when(ex is System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException or
                System.Text.Json.JsonException or InvalidDataException or ArgumentException or FormatException or OverflowException)
            {
                reason=ct.IsCancellationRequested?"进程停止；停机期间成交未回补":"公共成交连接中断或校验失败；缺口未回补";
                if(!ct.IsCancellationRequested)log.LogWarning("Binance {Instrument} public trade stream ended: {Type}",instrument,ex.GetType().Name);
            }
            finally {market.Changed-=ModeChanged;if(started)flow.End(reason,Now(),FlowRules.Binance,[instrument]);else if(!ct.IsCancellationRequested)flow.ReportSourceError(FlowRules.Binance,instrument,reason);}
            if(!ct.IsCancellationRequested)await Task.Delay(3000,ct);
        }
    }
}
