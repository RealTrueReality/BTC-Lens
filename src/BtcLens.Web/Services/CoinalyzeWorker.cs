using BtcLens.Infrastructure;

namespace BtcLens.Web.Services;

public sealed class CoinalyzeWorker(CoinalyzeEngine engine,CoinalyzeConnection connection) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            if(connection.Key is not null)await engine.RefreshAsync(stoppingToken);
            await Task.Delay(connection.Key is null?TimeSpan.FromSeconds(3):TimeSpan.FromSeconds(90),stoppingToken);
        }
    }
}
