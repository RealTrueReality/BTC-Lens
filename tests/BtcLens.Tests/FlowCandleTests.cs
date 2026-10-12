using BtcLens.Core;

namespace BtcLens.Tests;
public sealed class FlowCandleTests
{
    private const long T=1_800_000_000_000;
    [Fact] public void MinuteOhlcKeepsObservedIntraminuteExtremesAndNativeUsdt()
    {
        var contract=new InstrumentInfo(FlowRules.Perpetual,.1m,.01m,"BTC");
        var segment=new FlowSegment("s",FlowRules.Spot,T,T,"test",contract){BuyUsdt=0,SellUsdt=0};CvdPoint? point=null;
        foreach(var (n,size,side) in new[]{(1,2m,"buy"),(2,5m,"sell"),(3,1m,"buy")})
        {
            var update=FlowRules.Apply(segment,point,new(FlowRules.Spot,n,100,size,side,T+n*1000,1,"test",null),contract,T+n*1000);segment=update.Segment;point=update.Point;
        }
        Assert.Equal(0,point!.OpenBtc);Assert.Equal(2,point.HighBtc);Assert.Equal(-3,point.LowBtc);Assert.Equal(-2,point.CvdBtc);
        Assert.Equal(0,point.OpenUsdt);Assert.Equal(200,point.HighUsdt);Assert.Equal(-300,point.LowUsdt);
    }
    [Fact] public void SampledFiveMinuteOhlcUsesMinutePathAndDoesNotInventOneMinuteWicks()
    {
        var points=Points();var bar=Assert.Single(FlowCandles.Cvd(points,1,5));
        Assert.Equal(0,bar.Open);Assert.Equal(6,bar.High);Assert.Equal(-3,bar.Low);Assert.Equal(6,bar.Close);Assert.Equal(T,bar.OpenTime);Assert.Equal(T+300_000,bar.KnownAt);Assert.Equal(FlowCandles.Sampled,bar.Basis);
        Assert.Empty(FlowCandles.Cvd(points,1,1));
    }
    [Fact] public void IncompleteBucketOrSegmentChangeNeverBridgesAGap()
    {
        Assert.Empty(FlowCandles.Cvd(Points().Where(p=>p.Timestamp!=T+180_000).ToArray(),1,5));
        Assert.Empty(FlowCandles.Cvd(Points().Select((p,i)=>i>=2?p with {SegmentId="other"}:p).ToArray(),1,5));
        var one=Points()[0] with {Open=0,High=3,Low=-1};var exact=Assert.Single(FlowCandles.Cvd([one],0,1));Assert.Equal(FlowCandles.Observed,exact.Basis);Assert.Equal(3,exact.High);
    }
    [Fact] public void OiSampleCandlesRequireEndpointsAndDoNotFillMissingMinutes()
    {
        var values=new[]{100m,102m,99m,104m,103m,101m};var rows=values.Select((v,i)=>new FlowOiPoint(T+i*60_000,v)).ToArray();
        var bar=Assert.Single(FlowCandles.Oi(rows,5,60_000));Assert.Equal(100,bar.Open);Assert.Equal(104,bar.High);Assert.Equal(99,bar.Low);Assert.Equal(101,bar.Close);
        Assert.Empty(FlowCandles.Oi(rows.SkipLast(1).ToArray(),5,60_000));Assert.Empty(FlowCandles.Oi(rows.Where(p=>p.Timestamp!=T+180_000).ToArray(),5,60_000));
    }
    private static FlowDisplayPoint[] Points(){decimal previous=0;return new[]{2m,-3m,4m,1m,6m}.Select((v,i)=>{var delta=v-previous;previous=v;return new FlowDisplayPoint("s",FlowRules.Spot,T+(i+1)*60_000,v,delta){Volume=10};}).ToArray();}
}
