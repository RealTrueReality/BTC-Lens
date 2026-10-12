using BtcLens.Infrastructure;

namespace BtcLens.Web.Services;

public sealed class StudyLifecycleWorker(MarketEngine market,StudyStore studies):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested){studies.ObserveQuote(market.View);await Task.Delay(1000,stoppingToken);}
    }
}
