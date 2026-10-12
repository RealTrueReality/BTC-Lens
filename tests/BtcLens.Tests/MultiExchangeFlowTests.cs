using BtcLens.Core;
using BtcLens.Infrastructure;
namespace BtcLens.Tests;
public sealed class MultiExchangeFlowTests
{
    private const long T=1_790_000_040_000;
    private static readonly InstrumentInfo Contract=new(FlowRules.Perpetual,.1m,.01m,"BTC");
    private static FlowView Fixture()
    {
        var segments=FlowRules.Exchanges.SelectMany(e=>FlowRules.Instruments.Select(i=>new FlowSegment(e+i,i,T-60_000,T-60_000,"test",Contract,LastTradeAt:T+240_000,Messages:10){Exchange=e})).ToArray();
        var rows=segments.SelectMany(s=>Enumerable.Range(0,3).Select(n=>new CvdPoint(s.Id+n,s.Id,s.Instrument,T+n*60_000,T+n*60_000+59_000,0,s.Exchange==FlowRules.Okx?3:4,1,1){Exchange=s.Exchange,BuyUsdt=s.Exchange==FlowRules.Okx?300:400,SellUsdt=100})).ToArray();
        return new("live","test",null,segments,rows,[],T+180_000);
    }
    [Fact] public void BinanceParsingUsesTakerSideBaseQuantityAndPreservesAggregationEvidence()
    {
        const string json="""{"e":"aggTrade","E":1790000040000,"s":"BTCUSDT","a":40,"p":"65000.12","q":"0.00123","f":100,"l":102,"T":1790000040000,"m":true}""";
        var trade=BinancePublicTrades.Parse(json,FlowRules.Perpetual)!;
        Assert.Equal("sell",trade.Side);Assert.Equal(3,trade.Count);Assert.Equal(102,trade.TradeId);Assert.Equal(40,trade.AggregateId);
        Assert.Equal(.00123m,FlowRules.QuantityBtc(trade,Contract));Assert.Equal(FlowRules.Binance,trade.Exchange);Assert.Equal(json,trade.RawJson);
        var segment=new FlowSegment("binance",FlowRules.Perpetual,T,T,"test",Contract){Exchange=FlowRules.Binance};
        var result=FlowRules.Apply(segment,null,trade,Contract,T);
        Assert.Equal(-.00123m,result.Segment.CvdBtc);Assert.Equal(-79.9501476m,result.Point.CvdUsdt);
        Assert.Null(BinancePublicTrades.Parse("{\"result\":null,\"id\":1}",FlowRules.Spot));
        Assert.Throws<InvalidDataException>(()=>BinancePublicTrades.Parse(json.Replace("BTCUSDT","ETHUSDT"),FlowRules.Spot));
    }
    [Fact] public void ExchangeIdsDoNotCollideAndStoppingOneCollectorKeepsOthersActive()
    {
        var path=Path.Combine(Path.GetTempPath(),"btclens-multi-"+Guid.NewGuid().ToString("N"),"test.db");
        try
        {
            using var store=new LensStore(path);var engine=new OrderFlowEngine(store);
            engine.Begin(Contract,T);engine.Begin(Contract,T,FlowRules.Binance);
            var trade=new MarketTrade(FlowRules.Spot,100,65000,1,"buy",T,1,"test",null);
            engine.Accept(trade,T);engine.Accept(trade with {Exchange=FlowRules.Binance,AggregateId=1},T);engine.Flush(T+1000);
            Assert.Equal(1,engine.View.Latest(FlowRules.Spot)!.CvdBtc);Assert.Equal(1,engine.View.Latest(FlowRules.Spot,FlowRules.Binance)!.CvdBtc);
            Assert.NotNull(store.FindCapturedTrade("BTC-USDT:100"));Assert.NotNull(store.FindCapturedTrade("Binance:BTC-USDT:100"));
            engine.End("Binance断开",T+2000,FlowRules.Binance,[FlowRules.Spot]);
            engine.Accept(trade with {TradeId=101,Timestamp=T+2100},T+2100);
            engine.Accept(trade with {Instrument=FlowRules.Perpetual,Exchange=FlowRules.Binance,AggregateId=1,Timestamp=T+2100},T+2100);engine.Flush(T+3000);
            Assert.Equal(2,engine.View.Latest(FlowRules.Spot)!.CvdBtc);Assert.NotNull(engine.View.Latest(FlowRules.Spot,FlowRules.Binance)!.EndedAt);
            Assert.Null(engine.View.Latest(FlowRules.Perpetual,FlowRules.Binance)!.EndedAt);
            Assert.Throws<InvalidDataException>(()=>engine.Accept(trade with {TradeId=103,Instrument=FlowRules.Perpetual,Exchange=FlowRules.Binance,AggregateId=3,Timestamp=T+3100},T+3100));
        } finally {Directory.Delete(Path.GetDirectoryName(path)!,true);}
    }
    [Fact] public void CommonClosedMinutesSumDeltaAndStartTogetherWithoutSummingUnrelatedCvdBaselines()
    {
        var view=Fixture();var btc=FlowDisplay.Build(view,FlowRules.Exchanges,T,"BTC");
        Assert.Equal(T,btc.ActualStart);Assert.Equal(6,btc.Points.Length);Assert.Equal(0,btc.MissingMinutes);
        Assert.Equal(new[]{5m,10m,15m},btc.Points.Where(p=>p.Instrument==FlowRules.Spot).Select(p=>p.Cvd));
        Assert.All(btc.Points,p=>Assert.True(p.Timestamp<=view.AsOf));
        Assert.Equal(1500,FlowDisplay.Build(view,FlowRules.Exchanges,T,"USDT").Points.Last().Cvd);
        Assert.Equal(6,FlowDisplay.Build(view,[FlowRules.Okx],T,"BTC").Points.Last().Cvd);
    }
    [Fact] public void MissingExchangeMinuteBreaksAndRestartsCurveWithoutPartialSubstitution()
    {
        var view=Fixture();view=view with {Points=view.Points.Where(p=>!(p.Exchange==FlowRules.Binance && p.Instrument==FlowRules.Spot && p.Minute==T+60_000)).ToArray()};
        var result=FlowDisplay.Build(view,FlowRules.Exchanges,T,"BTC");
        var spot=result.Points.Where(p=>p.Instrument==FlowRules.Spot).ToArray();Assert.Equal(2,spot.Length);
        Assert.Equal(5,spot[1].Cvd);Assert.NotEqual(spot[0].SegmentId,spot[1].SegmentId);Assert.Equal(1,result.MissingMinutes);
    }
    [Fact] public void PartialFirstMinuteUnclosedFutureAndLegacyNotionalAreExcluded()
    {
        var view=Fixture();view=view with {AsOf=T+150_000,Segments=view.Segments.Select(s=>s with {StartedAt=T+1}).ToArray()};
        var result=FlowDisplay.Build(view,FlowRules.Exchanges,T,"BTC");
        Assert.Equal(2,result.Points.Length);Assert.Equal(T+60_000,result.ActualStart);Assert.All(result.Points,p=>Assert.Equal(T+120_000,p.Timestamp));
        view=view with {Points=view.Points.Select(p=>p with {BuyUsdt=null}).ToArray()};
        Assert.Empty(FlowDisplay.Build(view,FlowRules.Exchanges,T,"USDT").Points);
        Assert.Throws<ArgumentException>(()=>FlowDisplay.Build(view,["Unknown"],T,"BTC"));
    }
    [Fact] public void PartialEndedSegmentCannotHideACompleteMinuteFromAnotherSegment()
    {
        var view=Fixture();var existing=view.Segments.First(s=>s.Exchange==FlowRules.Okx && s.Instrument==FlowRules.Spot);
        var partial=existing with {Id="partial",EndedAt=T+20_000,LastTradeAt=T+19_000};
        var row=view.Points.First(p=>p.SegmentId==existing.Id && p.Minute==T) with {Id="partial-point",SegmentId="partial",Timestamp=T+19_000};
        var result=FlowDisplay.Build(view with {Segments=[..view.Segments,partial],Points=[..view.Points,row]},FlowRules.Exchanges,T,"BTC");
        Assert.Equal(6,result.Points.Length);Assert.Equal(0,result.MissingMinutes);
    }
}
