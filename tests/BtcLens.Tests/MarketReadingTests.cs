using BtcLens.Core;

namespace BtcLens.Tests;

public sealed class MarketReadingTests
{
    private const long End=1_800_000_000_000;
    public static MarketView Fixture()
    {
        var markets=new[]{new ProviderMarket("s.A","A",FlowRules.Spot,"USD","BTC"),new ProviderMarket("s.B","B",FlowRules.Spot,"USDT","BTC"),
            new ProviderMarket("p.A","A",FlowRules.Perpetual,"USDT","BTC"),new ProviderMarket("p.B","B",FlowRules.Perpetual,"USDT","BTC")};
        var rows=markets.SelectMany(m=>Enumerable.Range(0,61).Select(n=>new ProviderVolumeBar(m.Symbol,End-(61-n)*60_000,100,110,90,105,10,7))).ToArray();
        var oi=markets.Where(m=>m.Instrument==FlowRules.Perpetual).SelectMany(m=>Enumerable.Range(0,61).Select(n=>new ProviderOiBar(m.Symbol,End-(61-n)*60_000,100+n))).ToArray();
        var bars=Enumerable.Range(0,26).Select(n=>new Candle(FlowRules.Perpetual,"5m",End-(26-n)*300_000,100+n*.1m,101+n*.1m,99+n*.1m,100+(n+1)*.1m,10,true)).ToArray();
        return MarketView.Empty with {Mode="live",Scope="live",Cutoff=End+10_000,Quote=new(102.6m,End+10_000,"test"),Candles=new Dictionary<string,Candle[]>{{"5m",bars}},
            ProviderFlow=new(true,"test",null,End+10_000,markets,markets.Select(m=>m.Symbol).ToArray(),rows,oi),
            Zones=[new("region",FlowRules.Perpetual,"4H",ZoneKind.OrderBlock,Bias.Bullish,102,103,End-10_000_000,End-5_000_000,"test")]};
    }
    [Theory][InlineData(15)][InlineData(60)] public void SameWindowCombinesPositionSpotPerpetualAndNativeOiWithoutInventingDirection(int minutes)
    {
        var r=MarketReader.Build(Fixture(),[],minutes);Assert.Contains("现货与合约主动买入",r.Regime);
        Assert.Equal(minutes*8,r.Window.SpotDelta);Assert.Equal(40,r.Window.SpotImbalance);
        Assert.True(r.Window.OiPercent>0);Assert.Contains(r.Regions,p=>p.Id=="region");Assert.Contains(r.Conditions,s=>s.Contains("撤回"));
        Assert.Contains(r.Evidence,s=>s.Contains("无法识别多空"));Assert.Contains(r.Missing,s=>s.Contains("1D"));
    }
    [Fact] public void FallingOiChangesInterpretationAndDoesNotClaimLiquidationOrShortCoveringAsFact()
    {
        var v=Fixture();v=v with {ProviderFlow=v.ProviderFlow with {OpenInterest=v.ProviderFlow.OpenInterest.Select(o=>o with {Close=300-o.Close}).ToArray()}};
        var r=MarketReader.Build(v,[]);Assert.True(r.Window.OiPercent<0);Assert.Contains(r.Conflicts,s=>s.Contains("不能仅据此认定逼空、清算"));
    }
    [Fact] public void MissingMinuteResetsCvdAndSuppressesRegimeEvenWhenEndpointsExist()
    {
        var v=Fixture();v=v with {ProviderFlow=v.ProviderFlow with {Volumes=v.ProviderFlow.Volumes.Where(b=>b.Symbol!="s.A" || b.OpenTime!=End-5*60_000).ToArray()}};
        var r=MarketReader.Build(v,[]);Assert.Null(r.Window.SpotDelta);Assert.Contains("证据不足",r.Regime);Assert.NotNull(r.Window.PerpetualDelta);
    }
    [Theory][InlineData("stale")][InlineData("future")][InlineData("error")]
    public void UnavailableProviderCannotSilentlyFallBackToPublicData(string condition)
    {
        var v=Fixture();v=v with {ProviderFlow=v.ProviderFlow with {FetchedAt=condition=="stale"?End-180_001:condition=="future"?End+10_001:End,Error=condition=="error"?"failed":null}};
        var r=MarketReader.Build(v,[]);Assert.Null(r.Window.SpotDelta);Assert.Equal("Coinalyze",r.Window.Source);Assert.Contains(r.Missing,s=>s.Contains("Coinalyze"));
    }
    [Fact] public void FutureUnconfirmedBarsZonesAndDeletedWatchesAreExcluded()
    {
        var v=Fixture();var bars=v.Candles["5m"];
        v=v with {Quote=new(102.6m,End-40_000,"old"),Zones=[v.Zones[0] with {KnownAt=End+100_000}],Candles=new Dictionary<string,Candle[]>{{"5m",[..bars,bars[^1] with {OpenTime=End,Confirmed=false,Close=1000,High=1000}]}}};
        var w=new WatchZone("deleted","live",true,false,new(1,"old",102,103,"4H",Bias.Bullish,"",null,DateTimeOffset.FromUnixTimeMilliseconds(End-1000)),[]){DeletedAt=DateTimeOffset.FromUnixTimeMilliseconds(End)};
        var r=MarketReader.Build(v,[w]);Assert.Empty(r.Regions);Assert.Contains("证据不足",r.Regime);Assert.True(r.Window.PricePercent<1);
    }
    [Fact] public void MissingOiWithinWindowIsNotFilledOrComparedUsingUsdPriceEffect()
    {
        var v=Fixture();v=v with {ProviderFlow=v.ProviderFlow with {OpenInterest=v.ProviderFlow.OpenInterest.Where(o=>o.Symbol!="p.A" || o.OpenTime!=End-5*60_000).ToArray()}};
        var r=MarketReader.Build(v,[]);Assert.Null(r.Window.OiPercent);Assert.Contains(r.Missing,s=>s.Contains("OI 起点"));
    }
    [Fact] public void PublicCollectorWithoutRecentWindowReturnsMissingEvidenceInsteadOfThrowing()
    {
        var v=Fixture() with {ProviderFlow=ProviderFlowView.Empty,Flow=FlowView.Empty with {AsOf=End-86_400_000}};
        var r=MarketReader.Build(v,[]);Assert.Contains("证据不足",r.Regime);Assert.Equal("OKX + Binance 公共成交",r.Window.Source);
    }
}
