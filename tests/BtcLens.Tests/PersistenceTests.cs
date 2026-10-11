using BtcLens.Core;
using BtcLens.Infrastructure;
using BtcLens.Application;

namespace BtcLens.Tests;
public sealed class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),"btclens-tests-"+Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory,"test.db");
    [Fact] public void AlertAndTriggerPersistAtomicallyAndDeduplicateAcrossRestart()
    {
        var v=new WatchVersion(1,"test",100,110,"5m",Bias.Neutral,"",null,DateTimeOffset.UtcNow);
        var w=new WatchZone("watch","live",true,false,v,[]);
        var a=new AlertEvent(WatchRules.EventId(w),"live","watch",1,DateTimeOffset.UtcNow,105,"REST",v,"test","{}");
        using(var s=new LensStore(FilePath)) { s.SaveWatch(w); Assert.True(s.RecordAlert(w with {Triggered=true},a)); Assert.False(s.RecordAlert(w with {Triggered=true},a)); }
        using(var s=new LensStore(FilePath)) { Assert.Single(s.Alerts("live")); Assert.True(Assert.Single(s.Watches("live")).Triggered); Assert.NotNull(s.FindAlert(a.Id)); }
    }
    [Fact] public void CsvConflictRollsBackWholeImportAndDuplicateImportIsIdempotent()
    {
        using var s=new LensStore(FilePath); var t=TradeTests.Example() with {Id="one",ImportKey="one"};
        Assert.Equal(1,s.ImportTrades([t])); Assert.Equal(0,s.ImportTrades([t]));
        Assert.Throws<ArgumentException>(()=>s.ImportTrades([t with {Id="two",ImportKey="two"},t with {Review="conflict"}]));
        Assert.Single(s.Trades("live"));
    }
    [Fact] public void InitialRiskAndEntrySnapshotCannotBeOverwrittenByEditing()
    {
        using var s=new LensStore(FilePath); var t=TradeTests.Example() with {EntrySnapshotJson="original"};
        s.SaveTrade(t); Assert.Throws<ArgumentException>(()=>s.SaveTrade(t with {InitialStop=66800}));
        Assert.Throws<ArgumentException>(()=>s.SaveTrade(t with {EntrySnapshotJson="changed"}));
        s.SaveTrade(t with {Review="new review"}); Assert.Equal("new review",s.FindTrade(t.Id)!.Review);
    }
    [Fact] public async Task DemoEndToEndTouchesOnceKeepsSnapshotAndIsolatesNewSession()
    {
        using var s=new LensStore(FilePath); var engine=new MarketEngine(s,new OkxClient(new HttpClient()), useFixture: true);
        var oldScope=engine.View.Scope; var original=Assert.Single(s.Watches(oldScope));
        await engine.StepAsync(5); var a=Assert.Single(s.Alerts(oldScope)); Assert.Equal(original.Current,a.Zone);
        await engine.StepAsync(5); Assert.Single(s.Alerts(oldScope));
        var savedJson=a.EvidenceJson;
        await engine.SaveWatchAsync(original.Id,1,original.Current with {Lower=66900,Upper=67000});
        Assert.Equal(savedJson,s.FindAlert(a.Id)!.EvidenceJson); Assert.Equal(1,s.FindAlert(a.Id)!.Revision);
        await engine.NewReplayAsync(); Assert.NotEqual(oldScope,engine.View.Scope); Assert.Empty(s.Alerts(engine.View.Scope)); Assert.NotNull(s.FindAlert(a.Id));
    }
    [Fact] public async Task DeletedWatchStopsMonitoringAndDoesNotReturnAfterRestart()
    {
        string scope;
        using (var s = new LensStore(FilePath))
        {
            var engine = new MarketEngine(s, new OkxClient(new HttpClient()), useFixture: true);
            scope = engine.View.Scope; var watch = Assert.Single(s.Watches(scope));
            await engine.SwitchModeAsync("live");
            engine = new MarketEngine(s, new OkxClient(new HttpClient()), useFixture: true);
            await engine.SwitchModeAsync("demo");
            await engine.DeleteWatchAsync(watch.Id, watch.Current.Revision, scope);
            await engine.StepAsync(10);
            Assert.Empty(s.Watches(scope)); Assert.Empty(s.Alerts(scope));
            await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveWatchAsync(watch.Id, 1, watch.Current));
        }
        using (var s = new LensStore(FilePath))
        {
            var engine = new MarketEngine(s, new OkxClient(new HttpClient()), useFixture: true);
            Assert.Equal(scope, engine.View.Scope); Assert.Empty(s.Watches(scope));
            await engine.NewReplayAsync(); Assert.Single(s.Watches(engine.View.Scope));
        }
    }
    [Fact] public async Task DeletionKeepsEventAndLinkedTradeAndRejectsLateWrites()
    {
        string scope, eventId, eventJson, tradeJson;
        using (var s = new LensStore(FilePath))
        {
            var engine = new MarketEngine(s, new OkxClient(new HttpClient()), useFixture: true); scope = engine.View.Scope;
            var watch = Assert.Single(s.Watches(scope)); await engine.StepAsync(5);
            var alert = Assert.Single(s.Alerts(scope)); eventId = alert.Id; eventJson = JsonCodec.Write(alert);
            var trade = TradeTests.Example() with { Id = "linked", Mode = "demo", AlertId = eventId, EntrySnapshotJson = alert.EvidenceJson };
            s.SaveTrade(trade); tradeJson = JsonCodec.Write(trade);
            await engine.DeleteWatchAsync(watch.Id, 1, scope);
            Assert.Empty(s.Watches(scope));
            Assert.Throws<ArgumentException>(() => s.SaveWatch(watch));
            Assert.False(s.RecordAlert(watch with { Triggered = true }, alert with { Id = eventId + ":late" }));
            Assert.Single(s.Alerts(scope));
        }
        using (var s = new LensStore(FilePath))
        {
            var engine = new MarketEngine(s, new OkxClient(new HttpClient()), useFixture: true);
            Assert.Empty(s.Watches(scope));
            Assert.Equal(eventJson, JsonCodec.Write(s.FindAlert(eventId)));
            Assert.Equal(tradeJson, JsonCodec.Write(s.FindTrade("linked")));
        }
    }
    [Fact] public async Task DeleteRejectsChangedRevisionAndDifferentWorkspace()
    {
        using var s = new LensStore(FilePath); var engine = new MarketEngine(s, new OkxClient(new HttpClient()), useFixture: true);
        var scope = engine.View.Scope; var watch = Assert.Single(s.Watches(scope));
        await engine.SaveWatchAsync(watch.Id, 1, watch.Current with { Notes = "changed in another tab" });
        await Assert.ThrowsAsync<ArgumentException>(() => engine.DeleteWatchAsync(watch.Id, 1, scope));
        Assert.False(s.DeleteWatch("live", watch.Id, 2, DateTimeOffset.UtcNow));
        Assert.Equal(2, Assert.Single(s.Watches(scope)).Current.Revision);
        await engine.NewReplayAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.DeleteWatchAsync(watch.Id, 2, scope));
        Assert.Single(s.Watches(scope)); Assert.Single(s.Watches(engine.View.Scope));
    }
    public void Dispose()
    {
        // Only remove the literal unique directory created by this test instance.
        var resolved = Path.GetFullPath(directory);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("btclens-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup target escaped its owned temporary directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved,true);
    }
}
