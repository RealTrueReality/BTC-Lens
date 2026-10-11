using BtcLens.Application;
using BtcLens.Core;
using BtcLens.Infrastructure;

namespace BtcLens.Tests;

public sealed class PriceContextTests : IDisposable
{
    private static readonly InstrumentInfo Instrument = new("BTC-USDT-SWAP", .1m, .01m, "BTC");
    private static readonly long Start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    private static Candle Flat(int i, decimal price, decimal volume = 100) => new(Instrument.Id, "5m", Start + i * PriceContextRules.Step, price, price, price, price, volume, true);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "btclens-context-" + Guid.NewGuid().ToString("N"));
    [Fact] public void UniformProfileConservesBtcAndHasExplicitTieAndWholeRowArea()
    {
        var bars = new[] { Flat(0, 104) with { Low = 100, High = 108 }, Flat(1, 104, 300) with { Low = 100, High = 108 } };
        var r = PriceContextRules.Profile(bars, Start, Start + 2 * PriceContextRules.Step, Start + 2 * PriceContextRules.Step, Instrument, 8);
        Assert.Equal(4m, r.TotalBtc); Assert.Equal(4m, r.Rows.Sum(x => x.VolumeBtc));
        Assert.All(r.Rows, row => Assert.Equal(.5m, row.VolumeBtc));
        Assert.Equal(100.5m, r.Poc); Assert.Equal(100m, r.Val); Assert.Equal(106m, r.Vah);
        Assert.True(r.Coverage.Complete);
    }
    [Fact] public void FlatAndZeroVolumeBarsDoNotDivideByZeroOrInventLevels()
    {
        var r = PriceContextRules.Profile([Flat(0, 100)], Start, Start + PriceContextRules.Step, Start + PriceContextRules.Step, Instrument);
        Assert.Single(r.Rows); Assert.Equal(1m, r.TotalBtc);
        var zero = PriceContextRules.Profile([Flat(0, 100, 0)], Start, Start + PriceContextRules.Step, Start + PriceContextRules.Step, Instrument);
        Assert.Null(zero.Poc); Assert.Equal(0m, zero.TotalBtc);
        Assert.Null(PriceContextRules.Vwap([Flat(0, 100, 0)], Start + PriceContextRules.Step, Instrument, "day", 8).Value);
    }
    [Fact] public void DuplicateConflictsGapsAndInvalidGeometryRemainVisible()
    {
        var bars = new[] { Flat(0, 100), Flat(0, 100), Flat(1, 101), Flat(1, 102), Flat(3, 103) with { High = 102 }, Flat(4, 104) };
        var r = PriceContextRules.Profile(bars, Start, Start + 5 * PriceContextRules.Step, Start + 5 * PriceContextRules.Step, Instrument);
        Assert.Equal(2, r.Coverage.Observed); Assert.Equal(3, r.Coverage.Missing);
        Assert.Equal(1, r.Coverage.Conflicts); Assert.Equal(1, r.Coverage.Rejected); Assert.False(r.Coverage.Complete);
        Assert.Equal(2m, r.TotalBtc);
    }
    [Fact] public void VwapIsWeightedHlc3InBtcAndKnownOnlyAfterClose()
    {
        var bars = new[] { Flat(0, 100), Flat(1, 110, 300), Flat(2, 10000, 100000) };
        var r = PriceContextRules.Vwap(bars, Start + 2 * PriceContextRules.Step, Instrument, "day", 8);
        Assert.Equal(107.5m, r.Value); Assert.Equal(430m, r.WeightedSum); Assert.Equal(4m, r.VolumeBtc);
        Assert.Equal(new[] { Start + PriceContextRules.Step, Start + 2 * PriceContextRules.Step }, r.Points.Select(p => p.KnownAt));
        Assert.True(r.Coverage.Complete);
        Assert.Equal(100m, PriceContextRules.Vwap(bars, Start + 2 * PriceContextRules.Step - 1, Instrument, "day", 8).Value);
    }
    [Fact] public void UnconfirmedOrFutureDataCannotChangeHistoricalCalculations()
    {
        var bars = Enumerable.Range(0, 12).Select(i => Flat(i, 100 + i)).ToArray();
        for (var n = 2; n <= 12; n++)
        {
            var cutoff = Start + n * PriceContextRules.Step;
            var changed = bars.Take(n).Concat([Flat(n, 999999, 999999), Flat(n - 1, 999999) with { Confirmed = false }]).ToArray();
            Assert.Equal(JsonCodec.Write(PriceContextRules.Vwap(bars, cutoff, Instrument, "day", 8)), JsonCodec.Write(PriceContextRules.Vwap(changed, cutoff, Instrument, "day", 8)));
            Assert.Equal(JsonCodec.Write(PriceContextRules.Profile(bars, Start, cutoff, cutoff, Instrument)), JsonCodec.Write(PriceContextRules.Profile(changed, Start, cutoff, cutoff, Instrument)));
        }
    }
    [Theory]
    [InlineData("2026-10-11T12:00:00+08:00", "day", 8, "2026-10-11T00:00:00+08:00")]
    [InlineData("2026-10-11T12:00:00+08:00", "week", 8, "2026-10-05T00:00:00+08:00")]
    [InlineData("2026-10-01T00:00:00+08:00", "month", 0, "2026-09-01T00:00:00Z")]
    [InlineData("2028-03-01T00:00:00Z", "month", 0, "2028-03-01T00:00:00Z")]
    public void AnchorsUseCalendarMonthMondayAndExplicitTimezone(string at, string period, int offset, string expected)
    { Assert.Equal(DateTimeOffset.Parse(expected).ToUnixTimeMilliseconds(), PriceContextRules.Anchor(DateTimeOffset.Parse(at).ToUnixTimeMilliseconds(), period, offset)); }
    [Fact] public void NewPeriodDoesNotCarryPreviousVwap()
    {
        var next = Start + 86_400_000;
        var r = PriceContextRules.Vwap([Flat(0, 100)], next, Instrument, "day", 8);
        Assert.Null(r.Value); Assert.Empty(r.Points); Assert.Equal(next, r.Anchor);
    }
    [Fact] public void MissingVwapBarsBreakCurveAndPreventCompleteClaim()
    {
        var r = PriceContextRules.Vwap([Flat(0, 100), Flat(2, 110)], Start + 3 * PriceContextRules.Step, Instrument, "day", 8);
        Assert.False(r.Coverage.Complete); Assert.Equal(1, r.Coverage.Missing);
        Assert.NotEqual(r.Points[0].Segment, r.Points[1].Segment);
        var market = MarketView.Empty with { Cutoff = Start + 3 * PriceContextRules.Step, PriceContext = PriceContextView.Empty with { Vwaps = [r] } };
        Assert.Contains(AnalysisReport.Build(market, []).Missing, s => s.Contains("日 VWAP"));
    }
    [Fact] public void ReportCannotExportFutureVwapOrRetrospectivelySelectedProfile()
    {
        var end = Start + 2 * PriceContextRules.Step;
        var r = PriceContextRules.Profile([Flat(0, 100), Flat(1, 101)], Start, end, end, Instrument);
        var p = new SavedProfile("future-selection", "live", "late", "5m", end, r, []);
        var v = PriceContextRules.Vwap([Flat(0, 100), Flat(1, 101)], end, Instrument, "day", 8);
        var market = MarketView.Empty with { Cutoff = Start + PriceContextRules.Step, PriceContext = PriceContextView.Empty with { Profiles = [p], Vwaps = [v] } };
        var packet = AnalysisReport.Build(market, []);
        Assert.DoesNotContain("future-selection", JsonCodec.Write(packet.PriceContext));
        Assert.DoesNotContain("weightedSum", JsonCodec.Write(packet.PriceContext));
    }
    [Fact] public void ProfileRejectsFutureRangeMisalignmentAndWrongContractMetadata()
    {
        Assert.Throws<ArgumentException>(() => PriceContextRules.Profile([Flat(0, 100)], Start, Start + PriceContextRules.Step, Start, Instrument));
        Assert.Throws<ArgumentException>(() => PriceContextRules.Profile([Flat(0, 100)], Start + 1, Start + PriceContextRules.Step, Start + PriceContextRules.Step, Instrument));
        Assert.Throws<ArgumentException>(() => PriceContextRules.Vwap([Flat(0, 100)], Start + PriceContextRules.Step, Instrument with { ContractCurrency = "USD" }, "day", 8));
    }
    [Fact] public async Task ProfileSourceWatchAndEventStayFrozenAcrossRecalculationAndRestart()
    {
        var path = Path.Combine(directory, "test.db"); string profileId, evidence, scope;
        using (var store = new LensStore(path))
        {
            var market = new MarketEngine(store, new OkxClient(new HttpClient()), useFixture: true); var state = market.View; scope = state.Scope;
            var p = await market.SaveProfileAsync("range", "4H", state.Cutoff - 6 * 3_600_000, state.Cutoff, 40, 70, scope); profileId = p.Id;
            Assert.Equal(p.Result.SourceHash, PriceContextRules.Hash(p.SourceBars));
            var w = Assert.Single(store.Watches(scope));
            var reference = new PriceReference("profile", p.Id, "POC", p.Result.Poc!.Value, p.CreatedAt, p.Result.Algorithm, p.Result.SourceHash);
            await market.SaveWatchAsync(w.Id, 1, w.Current with { PriceReference = reference });
            await market.StepAsync(5); var alert = Assert.Single(store.Alerts(scope)); evidence = alert.EvidenceJson;
            Assert.Equal(reference, alert.Zone.PriceReference); Assert.Contains(p.Id, evidence); Assert.Contains("priceContext", evidence);
            await market.SetVwapOffsetAsync(0); await market.SaveProfileAsync("new", "5m", state.Cutoff - 3_600_000, state.Cutoff, 80, 80, scope);
            Assert.Equal(evidence, store.FindAlert(alert.Id)!.EvidenceJson);
            Assert.Throws<ArgumentException>(() => store.SaveProfile(p with { Name = "overwrite" }));
            await market.NewReplayAsync(); Assert.Empty(market.View.PriceContext.Profiles);
            await Assert.ThrowsAsync<ArgumentException>(() => market.SaveProfileAsync("wrong", "5m", state.Cutoff - 3_600_000, state.Cutoff, 40, 70, scope));
        }
        using (var store = new LensStore(path)) { var p = store.Profiles(scope).Single(p => p.Id == profileId); Assert.Equal(p.Result.SourceHash, PriceContextRules.Hash(p.SourceBars)); Assert.Equal(evidence, Assert.Single(store.Alerts(scope)).EvidenceJson); }
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("btclens-context-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
