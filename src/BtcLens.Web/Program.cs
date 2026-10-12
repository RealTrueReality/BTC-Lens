using BtcLens.Web.Components;
using BtcLens.Application;
using BtcLens.Infrastructure;
using BtcLens.Web.Services;
using MudBlazor.Services;
using Serilog;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Host.UseSerilog((ctx, services, logger) => logger.MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console().WriteTo.File(Path.Combine(ctx.HostingEnvironment.ContentRootPath, "logs", "lens-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
var databasePath=Path.GetFullPath(builder.Configuration["DatabasePath"] ?? Path.Combine(builder.Environment.ContentRootPath,"App_Data","btclens.db"));
builder.Services.AddSingleton<ILensStore>(_ => new LensStore(databasePath));
var protection=builder.Services.AddDataProtection().SetApplicationName("BtcLens").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(databasePath)!,"data-protection-keys")));
if(OperatingSystem.IsWindows())protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<CoinalyzeConnection>();
builder.Services.AddSingleton<StudyStore>();
builder.Services.AddSingleton<ModelConnection>();
var qianwenHttp=builder.Services.AddHttpClient<QianwenClient>(client=>{client.BaseAddress=new Uri(QianwenClient.BaseUrl);client.Timeout=TimeSpan.FromSeconds(120);})
    .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
var coinalyzeHttp=builder.Services.AddHttpClient<CoinalyzeClient>(client=>{client.BaseAddress=new Uri("https://api.coinalyze.net/v1/");client.Timeout=TimeSpan.FromSeconds(15);});
builder.Services.AddSingleton<CoinalyzeEngine>(services=>new(services.GetRequiredService<CoinalyzeClient>(),services.GetRequiredService<ILensStore>(),()=>services.GetRequiredService<CoinalyzeConnection>().Key));
var offlineFixture = builder.Environment.IsEnvironment("Testing") && builder.Configuration.GetValue<bool>("OfflineFixture");
var okxHttp = builder.Services.AddHttpClient<OkxClient>(client => { client.BaseAddress = new Uri(builder.Configuration["Okx:RestUrl"] ?? "https://www.okx.com/"); client.Timeout = TimeSpan.FromSeconds(8); });
if (offlineFixture)
{
    builder.Services.AddSingleton<OfflineNarrativeHandler>();
    qianwenHttp.ConfigurePrimaryHttpMessageHandler(services=>services.GetRequiredService<OfflineNarrativeHandler>());
    builder.Services.AddSingleton<OfflineCoinalyzeHandler>();
    coinalyzeHttp.ConfigurePrimaryHttpMessageHandler(services=>services.GetRequiredService<OfflineCoinalyzeHandler>());
    builder.Services.AddSingleton<OfflineHistoryHandler>();
    okxHttp.ConfigurePrimaryHttpMessageHandler(services => services.GetRequiredService<OfflineHistoryHandler>());
}
builder.Services.AddSingleton<MarketEngine>(services => new(services.GetRequiredService<ILensStore>(),
    services.GetRequiredService<OkxClient>(), services.GetRequiredService<OrderFlowEngine>(), useFixture: offlineFixture,
    coinalyze:services.GetRequiredService<CoinalyzeEngine>()));
builder.Services.AddSingleton<OrderFlowEngine>();
if (!offlineFixture)
{
    builder.Services.AddHostedService<MarketWorker>();
    builder.Services.AddHostedService<OrderFlowWorker>();
    builder.Services.AddHostedService<BinanceFlowWorker>();
    builder.Services.AddHostedService<PriceContextWorker>();
    builder.Services.AddHostedService<CoinalyzeWorker>();
    builder.Services.AddHostedService<StudyLifecycleWorker>();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/healthz", (MarketEngine market) =>
{
    var state = market.View;
    return Results.Json(new { app = "BTC Lens", mode = state.Mode, status = state.Status, transport = state.Transport,
        instrument = state.Instrument?.Id, quoteTimestamp = state.Quote?.Timestamp,
        candleCounts = state.Candles.ToDictionary(x => x.Key, x => x.Value.Length),
        priceContext = new { state.PriceContext.Status, state.PriceContext.Error, state.PriceContext.OffsetHours,
            candles = state.ContextCandles.Length, profiles = state.PriceContext.Profiles.Length,
            vwaps = state.PriceContext.Vwaps.Select(v => new { v.Period, v.Anchor, v.KnownAt, v.Value, v.Coverage }) },
        coinalyze = new {state.ProviderFlow.Configured,state.ProviderFlow.Status,state.ProviderFlow.Error,state.ProviderFlow.FetchedAt,state.ProviderFlow.Selected,volumeBars=state.ProviderFlow.Volumes.Length,oiBars=state.ProviderFlow.OpenInterest.Length},
        orderFlow = new { state.Flow.Status, state.Flow.Error, state.Flow.OiError, state.Flow.AsOf,
            instruments = BtcLens.Core.FlowRules.Instruments.Select(i => state.Flow.Latest(i)).Where(s => s is not null),
            sources = BtcLens.Core.FlowRules.Exchanges.SelectMany(e => BtcLens.Core.FlowRules.Instruments.Select(i => state.Flow.Latest(i,e))).Where(s => s is not null), state.Flow.SourceErrors,
            cvdPoints = state.Flow.Points.Length, oiSamples = state.Flow.OpenInterest.Length,
            latestOi = state.Flow.OpenInterest.LastOrDefault() } });
});
app.MapGet("/downloads/trades-template.csv", () => Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(TradeCsv.Template)).ToArray(), "text/csv", "btclens-trades-template.csv"));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Deterministic browser fixtures are available only in an explicitly isolated test host.
if (offlineFixture)
{
    app.MapPost("/test/study-quote/{price:decimal}",async(decimal price,MarketEngine market)=>{await market.SwitchModeAsync("live");await market.RefreshLiveAsync(default);await market.AcceptLiveQuoteAsync(new(price,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"isolated fixture"),default);return Results.Ok();});
    app.MapGet("/test/coinalyze-stats",(OfflineCoinalyzeHandler handler,ILensStore store)=>Results.Ok(new {handler.Requests,
        protectedKey=store.GetSetting("coinalyze:key") is {} value && value.Length>0 && value!=OfflineCoinalyzeHandler.Key,
        snapshotContainsKey=(store.GetSetting("coinalyze:snapshot")??"").Contains(OfflineCoinalyzeHandler.Key)}));
    app.MapPost("/test/replay", async (MarketEngine market) => { await market.SwitchModeAsync("demo"); await market.NewReplayAsync(); return Results.Ok(new { market.View.Scope, market.View.Cutoff }); });
    app.MapPost("/test/step/{count:int}", async (int count, MarketEngine market) => { await market.StepAsync(count); return Results.Ok(new { market.View.Cutoff }); });
    app.MapPost("/test/live-history", async (MarketEngine market) => { await market.SwitchModeAsync("live"); await market.RefreshLiveAsync(default); return Results.Ok(new { market.View.Scope }); });
    app.MapGet("/test/history-stats", (OfflineHistoryHandler history) => Results.Ok(new { requests = history.HistoryRequests }));
    app.MapPost("/test/flow-fixture", async (MarketEngine market,OrderFlowEngine flow) =>
    {
        await market.SwitchModeAsync("live");await market.RefreshLiveAsync(default);
        var start=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/60_000*60_000-8*60_000;
        foreach(var exchange in BtcLens.Core.FlowRules.Exchanges)
        {
            flow.End("隔离测试重置",start-1000,exchange);
            flow.Begin(new(BtcLens.Core.FlowRules.Perpetual,.1m,.01m,"BTC"),start,exchange);
            foreach(var instrument in BtcLens.Core.FlowRules.Instruments)
            {
                var baseId=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*1000+Random.Shared.Next(0,900);
                for(var n=0;n<31;n++)
                {
                    var time=start+n*15_000;
                    flow.Accept(new(instrument,baseId+n,67000,n%2==0?2:1,n%3==0?"sell":"buy",time,1,"isolated fixture",null)
                        {Exchange=exchange,AggregateId=exchange==BtcLens.Core.FlowRules.Binance?n+1:null},time);
                }
            }
        }
        flow.Flush(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        flow.AcceptOpenInterest(new(BtcLens.Core.FlowRules.Perpetual,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),100000,1000,67000000,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"isolated fixture"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return Results.Ok();
    });
}

app.Run();
