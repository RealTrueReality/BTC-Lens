using BtcLens.Core;
using BtcLens.Infrastructure;
namespace BtcLens.Tests;
public sealed class TrendWatchTests
{
    private const long Now = 1_790_000_000_000;
    private static WatchZone Zone() => new("trend", "live", true, false,
        new(1, "趋势线", 90, 120, "5m", Bias.Neutral, "", null, DateTimeOffset.FromUnixTimeMilliseconds(Now - 20_000))
        { Trend = new(Now - 60_000, Now, 90, 110, 2) }, [], new(114, Now - 1_000, "WebSocket"));
    [Fact] public void SlopedBandMovesWithTimeAndDoesNotUseFrozenBounds()
    {
        var v = Zone().Current;
        Assert.Equal(new WatchBounds(108,112,110), WatchRules.Bounds(v, Now));
        Assert.Equal(new WatchBounds(128,132,130), WatchRules.Bounds(v, Now+60_000));
        Assert.False(WatchRules.Inside(v, 115, Now)); Assert.True(WatchRules.Inside(v, 130, Now+60_000));
        Assert.Null(WatchRules.Bounds(v, Now-60_001));
        Assert.Equal("approach", WatchRules.Transition(Zone(), new(111,Now,"WebSocket"), Now, true));
    }
    [Fact] public void CrossingCanJumpOverTheEntireBandAndTracksTheMovingLine()
    {
        Assert.Equal("cross", WatchRules.Transition(Zone(), new(105,Now,"WebSocket"), Now, true));
        var stationary = Zone() with { LastSample = new(109,Now-5_000,"WebSocket") };
        Assert.Equal("cross", WatchRules.Transition(stationary,new(109,Now,"WebSocket"),Now,true));
        Assert.Null(WatchRules.Transition(Zone() with { LastSample = new(111,Now-1_000,"WebSocket") },new(111,Now,"WebSocket"),Now,true));
    }
    [Fact] public void CrossingRequiresFreshContinuousObservationsAndValidLine()
    {
        foreach(var z in new[]{Zone() with {LastSample=null},Zone() with {Triggered=true},Zone() with {Enabled=false},Zone() with {LastSample=new(114,Now-31_000,"REST")},Zone() with {DeletedAt=DateTimeOffset.UtcNow}})
            Assert.False(WatchRules.Entered(z,new(105,Now,"WebSocket"),Now,true));
        Assert.False(WatchRules.Entered(Zone(),new(105,Now,"REST"),Now,false));
        Assert.False(WatchRules.Entered(Zone(),new(105,Now-1000,"REST"),Now,true));
        Assert.Null(WatchRules.Bounds(Zone().Current with {Trend=new(Now-60_000,Now,110,10,2)},Now+60_000));
        Assert.Throws<ArgumentException>(()=>WatchRules.Validate(Zone().Current with {Trend=Zone().Current.Trend! with {End=Now-60_000}}));
        Assert.Throws<ArgumentException>(()=>WatchRules.Validate(Zone().Current with {Trend=Zone().Current.Trend! with {Tolerance=0}}));
    }
    [Fact] public async Task PromotedTrendRetainsGeometryAndImmutableEventAcrossEditsRestartAndDeletion()
    {
        var path=Path.Combine(Path.GetTempPath(),"btclens-trend-"+Guid.NewGuid().ToString("N"),"test.db");
        string eventJson, id;
        try
        {
            using(var store=new LensStore(path))
            {
                var engine=new MarketEngine(store,new OkxClient(new HttpClient()));
                var now=engine.View.Cutoff;
                var drawing=await engine.SaveDrawingAsync(new(null,null,"TrendLine",now-60_000,now,100,110,"live"));
                var watch=await engine.SaveWatchAsync(null,null,Zone().Current with {Trend=new(drawing.Start,drawing.End,drawing.Price1,drawing.Price2,2),ChartAnchor=new(drawing.Id,drawing.Start,drawing.End,drawing.Revision)},"live"); id=watch.Id;
                Assert.Empty(store.Drawings("live"));
                await engine.AcceptLiveQuoteAsync(new(114,now+1000,"WebSocket"),default);
                await engine.AcceptLiveQuoteAsync(new(105,now+2000,"WebSocket"),default);
                var alert=Assert.Single(store.Alerts("live"));eventJson=JsonCodec.Write(alert);
                Assert.Contains("穿越趋势线",alert.Summary); Assert.Contains("observedBounds",alert.EvidenceJson);
                Assert.Equal(WatchRules.TrendVersion,System.Text.Json.JsonDocument.Parse(alert.EvidenceJson).RootElement.GetProperty("rule").GetString());
                await engine.AcceptLiveQuoteAsync(new(113,now+3000,"WebSocket"),default);Assert.Single(store.Alerts("live"));
                var edited=await engine.SaveWatchAsync(id,1,watch.Current with {Trend=watch.Current.Trend! with {Tolerance=5}},"live");
                Assert.Equal(2,edited.Current.Revision);Assert.Equal(2,edited.History.Single().Trend!.Tolerance);
                Assert.Equal(eventJson,JsonCodec.Write(store.Alerts("live").Single()));
                await engine.DeleteWatchAsync(id,2,"live");
            }
            using(var store=new LensStore(path)) { Assert.Empty(store.Watches("live"));Assert.Equal(eventJson,JsonCodec.Write(store.Alerts("live").Single()));Assert.Empty(store.Drawings("live")); }
        }
        finally {Directory.Delete(Path.GetDirectoryName(path)!,true);}
    }
}
