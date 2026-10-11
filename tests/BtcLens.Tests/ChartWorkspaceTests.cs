using BtcLens.Application;
using BtcLens.Core;
using BtcLens.Infrastructure;
namespace BtcLens.Tests;
public sealed class ChartWorkspaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "btclens-chart-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "test.db");
    [Fact] public async Task ProductionAlwaysStartsLiveWithoutSeedingAndRejectsReplay()
    {
        using var store = new LensStore(PathName); store.SetSetting("market-mode", "demo");
        var engine = new MarketEngine(store, new OkxClient(new HttpClient()));
        Assert.Equal("live", engine.View.Mode); Assert.Equal("live", engine.View.Scope);
        Assert.Empty(store.Trades("demo")); Assert.Empty(store.Watches("live"));
        Assert.Null(store.GetSetting("demo-session"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SwitchModeAsync("demo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.NewReplayAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StepAsync());
    }
    [Theory]
    [InlineData(DrawingKind.Rectangle)]
    [InlineData(DrawingKind.HorizontalLine)]
    [InlineData(DrawingKind.TrendLine)]
    public async Task MultipleDrawingsPersistChangesAndRejectStaleOrDeletedWrites(DrawingKind kind)
    {
        string id;
        using (var store = new LensStore(PathName))
        {
            var engine = new MarketEngine(store, new OkxClient(new HttpClient()));
            var start = engine.View.Cutoff - 3_600_000;
            var drawing = await engine.SaveDrawingAsync(new(null, null, kind.ToString(), start, start + 600_000, 67000, kind == DrawingKind.HorizontalLine ? 67000 : 67100, "live")); id = drawing.Id;
            var second = await engine.SaveDrawingAsync(new(null, null, "Rectangle", start, start + 900_000, 66500, 66600, "live"));
            var moved = new DrawingChange(id, 1, kind.ToString(), start + 300_000, start + 900_000, 67050, kind == DrawingKind.HorizontalLine ? 67050 : 67150, "live");
            await engine.SaveDrawingAsync(moved);
            await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveDrawingAsync(moved));
            Assert.False(store.DeleteDrawing("other", id, 2, engine.View.Cutoff));
            await engine.DeleteDrawingAsync(second.Id, 1, "live");
            Assert.Single(store.Drawings("live"));
            await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveDrawingAsync(new(second.Id, 1, "Rectangle", start, start + 900_000, 66500, 66700, "live")));
            await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveDrawingAsync(moved with { Scope = "other", Revision = 2 }));
        }
        using (var store = new LensStore(PathName)) { var d = Assert.Single(store.Drawings("live")); Assert.Equal(id,d.Id); Assert.Equal(2,d.Revision); Assert.Equal(67050,d.Price1); }
    }
    [Fact] public async Task ProfileVisibilityAndDeletionKeepFrozenEvidenceAndReferencesAcrossRestart()
    {
        string scope, id, source, eventJson;
        using (var store = new LensStore(PathName))
        {
            var engine = new MarketEngine(store, new OkxClient(new HttpClient()), useFixture: true); var state = engine.View; scope = state.Scope;
            var p = await engine.SaveProfileAsync("A", "5m", state.Cutoff - 3_600_000, state.Cutoff, 40, 70, scope);
            id = p.Id; source = JsonCodec.Write(new { p.Result, p.SourceBars });
            for (var i = 0; i < 24; i++) store.SaveProfile(p with { Id = "range-" + i, Name = "range " + i });
            Assert.Equal(25, store.Profiles(scope).Length);
            var watch = Assert.Single(store.Watches(scope));
            await engine.SaveWatchAsync(watch.Id, 1, watch.Current with { PriceReference = new("profile", id, "A POC", p.Result.Poc!.Value, p.CreatedAt, p.Result.Algorithm, p.Result.SourceHash) });
            await engine.StepAsync(5); var alert = Assert.Single(store.Alerts(scope)); eventJson = JsonCodec.Write(alert);
            await engine.SetProfileVisibilityAsync(id, false, scope);
            var hidden = store.Profiles(scope).Single(x => x.Id == id);
            Assert.False(hidden.Visible); Assert.Equal(source, JsonCodec.Write(new { hidden.Result, hidden.SourceBars }));
            await engine.DeleteProfileAsync(id, scope); Assert.Equal(24, store.Profiles(scope).Length);
            Assert.False(store.SetProfileState(scope, id, true, null));
            Assert.Throws<ArgumentException>(() => store.SaveProfile(p));
            Assert.Equal(eventJson, JsonCodec.Write(store.FindAlert(alert.Id)));
            Assert.Equal(id, Assert.Single(store.Watches(scope)).Current.PriceReference!.EvidenceId);
        }
        using (var store = new LensStore(PathName))
        {
            Assert.Equal(24, store.Profiles(scope).Length); Assert.DoesNotContain(store.Profiles(scope), p => p.Id == id);
            Assert.Equal(eventJson, JsonCodec.Write(Assert.Single(store.Alerts(scope))));
        }
    }
    [Fact] public async Task DrawnWatchKeepsOriginalGeometryInAlertAfterDraggingOrDeletingObjects()
    {
        using var store = new LensStore(PathName); var engine = new MarketEngine(store, new OkxClient(new HttpClient()), useFixture: true);
        var state = engine.View; var watch = Assert.Single(store.Watches(state.Scope));
        var drawing = await engine.SaveDrawingAsync(new(null, null, "Rectangle", state.Cutoff - 900_000, state.Cutoff, watch.Current.Upper, watch.Current.Lower, state.Scope));
        var anchor = new ChartAnchor(drawing.Id, drawing.Start, drawing.End);
        await engine.SaveWatchAsync(watch.Id, 1, watch.Current with { ChartAnchor = anchor });
        await engine.StepAsync(5); var alert = Assert.Single(store.Alerts(state.Scope)); var json = JsonCodec.Write(alert);
        Assert.Equal(anchor, alert.Zone.ChartAnchor);
        await engine.SaveWatchAsync(watch.Id, 2, watch.Current with { ChartAnchor = anchor with { Start = anchor.Start + 300_000 }, Lower = 66900, Upper = 67000 });
        await engine.DeleteDrawingAsync(drawing.Id, 1, state.Scope);
        Assert.Equal(json, JsonCodec.Write(store.FindAlert(alert.Id)));
        Assert.Equal(anchor, store.FindAlert(alert.Id)!.Zone.ChartAnchor);
    }
    [Theory]
    [InlineData(DrawingKind.Rectangle, 100, 100)]
    [InlineData(DrawingKind.HorizontalLine, 100, 110)]
    [InlineData(DrawingKind.TrendLine, -1, 110)]
    public void InvalidGeometryCannotBeSaved(DrawingKind kind, int price1, int price2)
    {
        using var store = new LensStore(PathName);
        Assert.Throws<ArgumentException>(() => store.SaveDrawing(new("invalid", "live", 1, "invalid", kind, 1000000, 1600000, price1, price2, 1600000), null));
        Assert.Empty(store.Drawings("live"));
    }
    [Fact] public async Task DrawingPromotionIsAtomicAndRejectsOutdatedGeometry()
    {
        using var store = new LensStore(PathName); var engine = new MarketEngine(store, new OkxClient(new HttpClient())); var state = engine.View;
        var d = await engine.SaveDrawingAsync(new(null, null, "Rectangle", state.Cutoff - 900_000, state.Cutoff, 67000, 67100, "live"));
        var version = new WatchVersion(0, "drawn watch", 67000, 67100, "5m", Bias.Neutral, "", null, default) { ChartAnchor = new(d.Id, d.Start, d.End, 1) };
        await engine.SaveDrawingAsync(new(d.Id, 1, "Rectangle", d.Start, d.End, 67000, 67200, "live"));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveWatchAsync(null, null, version));
        Assert.Empty(store.Watches("live")); Assert.Single(store.Drawings("live"));
        var watch = await engine.SaveWatchAsync(null, null, version with { Upper = 67200, ChartAnchor = version.ChartAnchor! with { DrawingRevision = 2 } });
        Assert.Single(store.Watches("live")); Assert.Empty(store.Drawings("live")); Assert.Equal(2, watch.Current.ChartAnchor!.DrawingRevision);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveWatchAsync(null, null, watch.Current));
        Assert.Single(store.Watches("live"));
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("btclens-chart-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved,true);
    }
}
