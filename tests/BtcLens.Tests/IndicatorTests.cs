using BtcLens.Core;
using BtcLens.Infrastructure;
using BtcLens.Application;

namespace BtcLens.Tests;
public sealed class IndicatorTests
{
    private static Candle Bar(int i, decimal open, decimal high, decimal low, decimal close, bool confirmed = true) =>
        new("BTC-USDT-SWAP", "5m", 1_000_000 + i * 300_000L, open, high, low, close, 10, confirmed);
    private static Candle[] FvgBars() => [Bar(0, 100, 105, 95, 103), Bar(1, 103, 114, 102, 113), Bar(2, 113, 120, 110, 118)];
    [Fact] public void FvgWaitsForThirdBarCloseAndConfirmation()
    {
        var bars = FvgBars();
        Assert.Empty(Indicators.Detect(bars, bars[2].CloseTime - 1));
        Assert.Empty(Indicators.Detect([bars[0], bars[1], bars[2] with { Confirmed = false }], bars[2].CloseTime));
        var zone = Assert.Single(Indicators.Detect(bars, bars[2].CloseTime));
        Assert.Equal(105m, zone.Lower); Assert.Equal(110m, zone.Upper); Assert.Equal(bars[2].CloseTime, zone.KnownAt);
    }
    [Fact] public void FutureExtremesDoNotChangeHistoricalResult()
    {
        var bars = FvgBars(); var cutoff = bars[^1].CloseTime;
        var original = Indicators.Detect(bars, cutoff);
        var extended = Indicators.Detect([.. bars, Bar(3, 118, 140, 90, 100)], cutoff);
        Assert.Equal(original, extended);
    }
    [Fact] public void EqualityIsNotFvg()
    {
        var bars = FvgBars(); bars[2] = bars[2] with { Low = 105 };
        Assert.Empty(Indicators.Detect(bars, bars[^1].CloseTime));
    }
    [Fact] public void TouchAndFillAreDistinctAndUseClosedBars()
    {
        var bars = FvgBars();
        var touched = Assert.Single(Indicators.Detect([.. bars, Bar(3, 118, 119, 108, 112)], bars[^1].CloseTime + 300_000), z => z.KnownAt == bars[^1].CloseTime);
        Assert.Equal(ZoneLife.Touched, touched.Life);
        var filled = Assert.Single(Indicators.Detect([.. bars, Bar(3, 118, 119, 105, 109)], bars[^1].CloseTime + 300_000), z => z.KnownAt == bars[^1].CloseTime);
        Assert.Equal(ZoneLife.Filled, filled.Life);
    }
    [Theory]
    [InlineData(ZoneKind.Fvg)]
    [InlineData(ZoneKind.OrderBlock)]
    public void ReportDropsUsedZoneOnlyAfterReturnBarClosesAndRetainsLifecycleEvidence(ZoneKind kind)
    {
        Candle[] bars = kind == ZoneKind.Fvg
            ? [.. FvgBars(), Bar(3, 118, 119, 108, 112)]
            : [Bar(0,100,104,96,101), Bar(1,101,106,98,104), Bar(2,104,110,100,105),
               Bar(3,105,107,99,102), Bar(4,102,106,98,103), Bar(5,103,108,97,101),
               Bar(6,101,112,100,111), Bar(7,111,112,107,111)];
        var before = MarketView.Empty with { Cutoff = bars[^1].CloseTime - 1, Zones = Indicators.Detect(bars, bars[^1].CloseTime - 1, 2) };
        var zone = Assert.Single(before.Zones, z => z.Kind == kind);
        Assert.Equal(ZoneLife.Active, zone.Life);
        Assert.Contains(AnalysisReport.Build(before, []).Observations, o => o.Contains(zone.Id));
        var after = before with { Cutoff = bars[^1].CloseTime, Zones = Indicators.Detect(bars, bars[^1].CloseTime, 2) };
        Assert.Equal(ZoneLife.Touched, Assert.Single(after.Zones, z => z.Id == zone.Id).Life);
        Assert.DoesNotContain(AnalysisReport.Build(after, []).Observations, o => o.Contains(zone.Id));
    }
    [Fact] public void GapMakesExistingLifecycleUnknownAndDoesNotCreateBridgedFvg()
    {
        var bars = FvgBars(); var gap = Bar(4, 118, 123, 116, 120);
        var result = Indicators.Detect([.. bars, gap], gap.CloseTime);
        Assert.Equal(ZoneLife.Unknown, Assert.Single(result).Life);
    }
    [Fact] public void BearishFvgMirrorsPriceGeometry()
    {
        Candle[] bars = [Bar(0, 120, 125, 115, 118), Bar(1, 118, 119, 105, 107), Bar(2, 107, 110, 100, 103)];
        var zone = Assert.Single(Indicators.Detect(bars, bars[^1].CloseTime));
        Assert.Equal(Bias.Bearish, zone.Bias); Assert.Equal(110m, zone.Lower); Assert.Equal(115m, zone.Upper);
    }
    [Fact] public void ObUsesConfirmedPivotAndLastOppositeCandle()
    {
        Candle[] bars = [Bar(0,100,104,96,101), Bar(1,101,106,98,104), Bar(2,104,110,100,105),
            Bar(3,105,107,99,102), Bar(4,102,106,98,103), Bar(5,103,108,97,101), Bar(6,101,112,100,111)];
        Assert.DoesNotContain(Indicators.Detect(bars, bars[5].CloseTime, 2), z => z.Kind == ZoneKind.OrderBlock);
        var zone = Assert.Single(Indicators.Detect(bars, bars[6].CloseTime, 2), z => z.Kind == ZoneKind.OrderBlock);
        Assert.Equal(bars[5].OpenTime, zone.SourceTime); Assert.Equal(97m, zone.Lower); Assert.Equal(108m, zone.Upper);
        Assert.Equal(bars[6].CloseTime, zone.KnownAt);
        var invalid = Indicators.Detect([..bars, Bar(7,111,112,95,96)], bars[6].CloseTime + 300_000, 2).Single(z => z.Id == zone.Id);
        Assert.Equal(ZoneLife.Invalidated, invalid.Life);
    }
    [Fact] public void DemoHigherTimeframesOnlyUseRevealedPrefix()
    {
        var bars = DemoMarket.Generate();
        var a = DemoMarket.Visible(bars, DemoMarket.InitialIndex);
        var altered = bars.Select((b,i) => i >= DemoMarket.InitialIndex ? b with { High = b.High + 100000 } : b).ToArray();
        var b = DemoMarket.Visible(altered, DemoMarket.InitialIndex);
        foreach (var tf in a.Keys) Assert.Equal(a[tf], b[tf]);
        Assert.All(a.Values.SelectMany(x => x), c => Assert.True(c.OpenTime < bars[DemoMarket.InitialIndex - 1].CloseTime));
    }
    [Fact] public void KeyLevelsDoNotUseOlderPeriodAsYesterday()
    {
        var start = new DateTimeOffset(2026,10,9,0,0,0,TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        var old = new Candle("BTC-USDT-SWAP","1D",start - 86_400_000,100,110,90,105,10,true);
        var result = Indicators.Levels(new Dictionary<string,Candle[]> { ["1D"] = [old] }, start + 86_400_000 + 1000);
        Assert.Empty(result);
    }
}
