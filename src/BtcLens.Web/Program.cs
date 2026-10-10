using BtcLens.Web.Components;
using BtcLens.Application;
using BtcLens.Infrastructure;
using BtcLens.Web.Services;
using MudBlazor.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Host.UseSerilog((ctx, services, logger) => logger.MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console().WriteTo.File(Path.Combine(ctx.HostingEnvironment.ContentRootPath, "logs", "lens-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddSingleton<ILensStore>(_ => new LensStore(builder.Configuration["DatabasePath"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "btclens.db")));
builder.Services.AddHttpClient<OkxClient>(client => { client.BaseAddress = new Uri(builder.Configuration["Okx:RestUrl"] ?? "https://www.okx.com/"); client.Timeout = TimeSpan.FromSeconds(8); });
builder.Services.AddSingleton<MarketEngine>();
builder.Services.AddSingleton<OrderFlowEngine>();
builder.Services.AddHostedService<MarketWorker>();
builder.Services.AddHostedService<OrderFlowWorker>();

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
        orderFlow = new { state.Flow.Status, state.Flow.Error, state.Flow.OiError, state.Flow.AsOf,
            instruments = BtcLens.Core.FlowRules.Instruments.Select(i => state.Flow.Latest(i)).Where(s => s is not null),
            cvdPoints = state.Flow.Points.Length, oiSamples = state.Flow.OpenInterest.Length,
            latestOi = state.Flow.OpenInterest.LastOrDefault() } });
});
app.MapGet("/downloads/trades-template.csv", () => Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(TradeCsv.Template)).ToArray(), "text/csv", "btclens-trades-template.csv"));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
