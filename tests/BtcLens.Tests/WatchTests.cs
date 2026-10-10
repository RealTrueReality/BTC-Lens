using BtcLens.Core;
namespace BtcLens.Tests;
public sealed class WatchTests
{
    private const long Now = 1_790_000_000_000;
    private static WatchZone Zone() => new("zone","live",true,false,new(1,"test",100,110,"5m",Bias.Neutral,"",null,DateTimeOffset.FromUnixTimeMilliseconds(Now-20_000)),[],new(95,Now-1000,"WebSocket"));
    [Fact] public void FirstObservedEntryIncludesBoundary() { Assert.True(WatchRules.Entered(Zone(),new(100,Now,"WebSocket"),Now,true)); }
    [Fact] public void AlreadyInsideDoesNotInventAnEntry() { Assert.False(WatchRules.Entered(Zone() with {LastSample=new(105,Now-1000,"WebSocket")},new(108,Now,"WebSocket"),Now,true)); }
    [Fact] public void FirstSampleAndDiscontinuityDoNotTrigger()
    {
        Assert.False(WatchRules.Entered(Zone() with {LastSample=null},new(105,Now,"WebSocket"),Now,true));
        Assert.False(WatchRules.Entered(Zone(),new(105,Now,"WebSocket"),Now,false));
        Assert.False(WatchRules.Entered(Zone() with {LastSample=new(95,Now-60_000,"REST")},new(105,Now,"REST"),Now,true));
    }
    [Fact] public void StaleDuplicateAndConsumedSamplesAreIgnored()
    {
        Assert.False(WatchRules.Entered(Zone(),new(105,Now-31_000,"REST"),Now,true));
        Assert.False(WatchRules.Entered(Zone(),new(105,Now-1000,"REST"),Now,true));
        Assert.False(WatchRules.Entered(Zone() with {Triggered=true},new(105,Now,"WebSocket"),Now,true));
    }
    [Fact] public void NewRevisionHasSeparateIdAndOldBoundsRemain()
    {
        var z=Zone(); var old=z.Current; var edited=z with {Current=old with {Revision=2,Lower=90},History=[old]};
        Assert.NotEqual(WatchRules.EventId(z),WatchRules.EventId(edited)); Assert.Equal(100,edited.History[0].Lower);
    }
    [Fact] public void ReplayAllowsExplicitFiveMinuteObservationCadence()
    {
        var z=Zone() with {Current=Zone().Current with {CreatedAt=DateTimeOffset.FromUnixTimeMilliseconds(Now-300_000)},LastSample=new(95,Now-300_000,"5m 收盘回放")};
        Assert.False(WatchRules.Entered(z,new(105,Now,"5m 收盘回放"),Now,true));
        Assert.True(WatchRules.Entered(z,new(105,Now,"5m 收盘回放"),Now,true,300_000));
    }
}
