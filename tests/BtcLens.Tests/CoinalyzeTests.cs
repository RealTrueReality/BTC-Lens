using System.Net;
using System.Text;
using BtcLens.Core;
using BtcLens.Infrastructure;

namespace BtcLens.Tests;

public sealed class CoinalyzeTests
{
    private const long End=1_800_000_000_000;
    private static readonly ProviderMarket[] Markets=[new("spot.A","A",FlowRules.Spot,"USD","BTC"),new("perp.B","B",FlowRules.Perpetual,"USDT","BTC")];
    [Fact] public async Task ClientChecksMetadataSendsHeaderAndRequestsNativeOiWithSeconds()
    {
        var handler=new Handler();var client=new CoinalyzeClient(new(handler){BaseAddress=new("https://api.coinalyze.net/v1/")});
        var markets=await client.Markets("test-secret",default);Assert.Equal(2,markets.Length);Assert.DoesNotContain(markets,m=>m.Symbol=="inverse.B");
        var result=await client.History("test-secret",markets,End,default);
        Assert.Equal(7,Assert.Single(result.Volumes).BuyVolume);Assert.Equal(100,Assert.Single(result.Oi).Close);
        Assert.All(handler.Requests,r=>{Assert.Equal("test-secret",r.Key);Assert.DoesNotContain("test-secret",r.Uri);});
        Assert.Contains(handler.Requests,r=>r.Uri.Contains("convert_to_usd=false"));
        Assert.Contains(handler.Requests,r=>r.Uri.Contains("from=1799913600") && r.Uri.Contains("to=1799999940"));
    }
    [Theory][InlineData("missing")][InlineData("negative")][InlineData("future")][InlineData("unknown")]
    public async Task ClientRejectsMissingBuyVolumeBadQuantityFutureTimestampAndWrongSymbol(string problem)
    {
        var handler=new Handler{Problem=problem};var client=new CoinalyzeClient(new(handler){BaseAddress=new("https://api.coinalyze.net/v1/")});
        await Assert.ThrowsAnyAsync<Exception>(()=>client.History("test-secret",Markets,End,default));
    }
    [Fact] public async Task ApiErrorDoesNotEchoCredentialOrResponseBody()
    {
        var handler=new Handler{Problem="auth"};var client=new CoinalyzeClient(new(handler){BaseAddress=new("https://api.coinalyze.net/v1/")});
        var ex=await Assert.ThrowsAsync<InvalidDataException>(()=>client.Markets("test-secret",default));
        Assert.DoesNotContain("test-secret",ex.Message);Assert.Contains("未授权",ex.Message);
    }
    [Fact] public void CommonProviderMinutesExcludeIncompleteDuplicatesAndMissingMarkets()
    {
        var view=new ProviderFlowView(true,"test",null,End,Markets,Markets.Select(m=>m.Symbol).ToArray(),
            Markets.SelectMany(m=>Enumerable.Range(0,3).Select(n=>new ProviderVolumeBar(m.Symbol,End-180_000+n*60_000,100,110,90,105,10,7))).ToArray(),
            [new("perp.B",End-180_000,100),new("perp.B",End-120_000,110)]);
        var full=ProviderFlow.Build(view,End-180_000,End);Assert.Equal(6,full.Points.Length);Assert.Equal(12,full.Points.Last().Cvd);
        Assert.Equal(110,full.OpenInterest.Last().Btc);Assert.All(full.Points,p=>Assert.Equal(10,p.Volume));
        var missing=view with {Volumes=view.Volumes.Where(b=>b.Symbol!="spot.A" || b.OpenTime!=End-120_000).ToArray()};
        var spot=ProviderFlow.Build(missing,End-180_000,End).Points.Where(p=>p.Instrument==FlowRules.Spot).ToArray();
        Assert.Equal(2,spot.Length);Assert.Equal(4,spot.Last().Cvd);Assert.NotEqual(spot.First().SegmentId,spot.Last().SegmentId);
        var duplicate=view with {Volumes=[..view.Volumes,view.Volumes[0]]};Assert.Equal(5,ProviderFlow.Build(duplicate,End-180_000,End).Points.Length);
        Assert.Equal(4,ProviderFlow.Build(view,End-180_000,End-1).Points.Length);
    }
    [Fact] public void RefreshBudgetAndSpotPerpetualSelectionAreExplicit()
    {
        Assert.Throws<ArgumentException>(()=>CoinalyzeClient.ValidateSelection([Markets[0]]));
        Assert.Throws<ArgumentException>(()=>CoinalyzeClient.ValidateSelection(Enumerable.Range(0,15).Select(n=>Markets[1] with {Symbol="p"+n}).Append(Markets[0]).ToArray()));
        CoinalyzeClient.ValidateSelection(Markets);
    }
    [Fact] public void DefaultVenuesSeparateSpotAndPerpetualAndDoNotSelectEmptyIntegrationMarkets()
    {
        var spot=new[]{"Binance","Coinbase","Bybit","Kraken","Bitfinex","BitFlyer"}.Select(n=>new ProviderMarket(n+".s",n,FlowRules.Spot,"USD","BTC"));
        var perps=new[]{"Binance","Coinbase","OKX","Bybit","Huobi","Deribit","dYdX","Kraken","Bitfinex"}.Select(n=>new ProviderMarket(n+".p",n,FlowRules.Perpetual,"USDT","BTC"));
        var all=spot.Concat(perps).Append(new("Coinbase.usdt","Coinbase",FlowRules.Spot,"USDT","BTC")).ToArray();
        var defaults=CoinalyzeClient.DefaultSelection(all);
        Assert.Equal(8,defaults.Length);Assert.Contains("Coinbase.s",defaults);Assert.DoesNotContain("Coinbase.usdt",defaults);
        Assert.DoesNotContain("BitFlyer.s",defaults);Assert.DoesNotContain("Coinbase.p",defaults);Assert.DoesNotContain("Kraken.p",defaults);Assert.DoesNotContain("Bitfinex.p",defaults);
        CoinalyzeClient.ValidateSelection(all.Where(m=>defaults.Contains(m.Symbol)).ToArray());
        Assert.Equal(2,CoinalyzeClient.DefaultSelection(Markets).Length);
    }
    [Fact] public async Task ProviderCacheSurvivesRestartButChangedSelectionCannotRestoreOldMarkets()
    {
        var path=Path.Combine(Path.GetTempPath(),"btclens-provider-"+Guid.NewGuid().ToString("N"),"test.db");
        try
        {
            using var store=new LensStore(path);var client=new CoinalyzeClient(new(new OfflineCoinalyzeHandler()){BaseAddress=new("https://api.coinalyze.net/v1/")});
            string? key=OfflineCoinalyzeHandler.Key;var engine=new CoinalyzeEngine(client,store,()=>key);
            var markets=await engine.VerifyAsync(key,default);engine.Configure(markets,markets.Select(m=>m.Symbol).ToArray());await engine.RefreshAsync(default);
            Assert.NotEmpty(engine.View.Volumes);var restored=new CoinalyzeEngine(client,store,()=>key);Assert.Equal(engine.View.Volumes,restored.View.Volumes);
            var reduced=markets.Where(m=>m.ExchangeCode=="A").Select(m=>m.Symbol).ToArray();engine.Configure(markets,reduced);
            restored=new CoinalyzeEngine(client,store,()=>key);Assert.Equal(reduced,restored.View.Selected);Assert.Empty(restored.View.Volumes);Assert.Equal(0,restored.View.FetchedAt);
            await engine.RefreshAsync(default);Assert.NotEmpty(engine.View.Volumes);var before=engine.View.Volumes;
            key="invalid";await engine.RefreshAsync(default);Assert.NotNull(engine.View.Error);Assert.Same(before,engine.View.Volumes);Assert.False(engine.View.Fresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            key=null;engine.Disconnect();Assert.False(new CoinalyzeEngine(client,store,()=>key).View.Configured);
            Assert.DoesNotContain(OfflineCoinalyzeHandler.Key,store.GetSetting("coinalyze:snapshot")??"");
        }finally{Directory.Delete(Path.GetDirectoryName(path)!,true);}
    }
    [Fact] public void UnknownSelectedMarketNeverTurnsIntoAPartialAggregate()
    {
        var view=new ProviderFlowView(true,"test",null,End,Markets,["spot.A","perp.B","missing.C"],[new("spot.A",End-60_000,100,110,90,105,10,7)],[new("perp.B",End-60_000,100)]);
        Assert.Empty(ProviderFlow.Build(view,End-60_000,End).Points);Assert.Empty(ProviderFlow.Build(view,End-60_000,End).OpenInterest);
    }
    private sealed class Handler:HttpMessageHandler
    {
        public string? Problem;public List<(string Uri,string Key)> Requests=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;Requests.Add((request.RequestUri.ToString(),request.Headers.GetValues("api_key").Single()));
            var json=path.EndsWith("exchanges")?"""[{"code":"A","name":"Binance"},{"code":"B","name":"OKX"}]""":
                path.EndsWith("spot-markets")?"""[{"symbol":"spot.A","exchange":"A","base_asset":"BTC","quote_asset":"USD","has_buy_sell_data":true}]""":
                path.EndsWith("future-markets")?"""[{"symbol":"perp.B","exchange":"B","base_asset":"BTC","quote_asset":"USDT","margined":"STABLE","oi_lq_vol_denominated_in":"BASE_ASSET","is_perpetual":true,"has_buy_sell_data":true,"has_ohlcv_data":true},{"symbol":"inverse.B","exchange":"B","base_asset":"BTC","quote_asset":"USD","margined":"COIN","oi_lq_vol_denominated_in":"QUOTE_ASSET","is_perpetual":true,"has_buy_sell_data":true,"has_ohlcv_data":true}]""":
                path.EndsWith("open-interest-history")?"""[{"symbol":"perp.B","history":[{"t":1799999940,"c":100}]}]""":
                """[{"symbol":"spot.A","history":[{"t":1799999940,"o":100,"h":110,"l":90,"c":105,"v":10,"bv":7}]}]""";
            if(Problem=="missing")json=json.Replace(",\"bv\":7","");if(Problem=="negative")json=json.Replace("\"bv\":7","\"bv\":11");
            if(Problem=="future")json=json.Replace("1799999940","1800000000");if(Problem=="unknown")json=json.Replace("spot.A","other.A");
            return Task.FromResult(new HttpResponseMessage(Problem=="auth"?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=new StringContent(Problem=="auth"?"test-secret":json,Encoding.UTF8,"application/json")});
        }
    }
}
