using System.Text.Json;
using BtcLens.Core;
using BtcLens.Infrastructure;

namespace BtcLens.Tests;

public sealed class OrderFlowTests : IDisposable
{
    private static readonly InstrumentInfo Contract = new(FlowRules.Perpetual, .1m, .01m, "BTC");
    private const long T = 1791641000000;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "btclens-flow-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "test.db");
    private static MarketTrade Trade(long id = 100, string instrument = FlowRules.Spot, string side = "buy", decimal size = .12m,
        long time = T, int count = 1, long seq = 50) => new(instrument, id, 67_000, size, side, time, count, "0", seq);
    private static FlowSegment Segment(string instrument = FlowRules.Spot) => new("one", instrument, T, T, "test", Contract);

    [Fact] public void AggregateCountDoesNotMultiplyQuantityAndPerpetualContractsConvertToBtc()
    {
        var spot = FlowRules.Apply(Segment(), null, Trade(count: 3), Contract, T + 50);
        Assert.Equal(.12m, spot.Segment.CvdBtc); Assert.Equal(3, spot.Segment.Matches);
        var perp = FlowRules.Apply(Segment(FlowRules.Perpetual), null, Trade(instrument: FlowRules.Perpetual, size: 12, count: 3), Contract, T);
        Assert.Equal(.12m, perp.Segment.CvdBtc);
        Assert.Throws<ArgumentException>(() => FlowRules.QuantityBtc(perp.Trade.Trade, Contract with { ContractCurrency = "USD" }));
    }
    [Fact] public void TakerSellSubtractsAndBucketTimestampTracksLastObservedEvidence()
    {
        var first = FlowRules.Apply(Segment(), null, Trade(), Contract, T);
        var second = FlowRules.Apply(first.Segment, first.Point, Trade(id: 101, side: "sell", size: .05m, time: T + 100), Contract, T + 100);
        Assert.Equal(.07m, second.Segment.CvdBtc); Assert.Equal(.12m, second.Point.BuyBtc); Assert.Equal(.05m, second.Point.SellBtc);
        Assert.Equal(T + 100, second.Point.Timestamp); Assert.NotEqual(second.Point.Minute, second.Point.Timestamp);
        Assert.Equal(2, second.Point.Messages);
    }
    [Fact] public void SameSequenceIdAndTimestampAreNotUsedToDeduplicateDistinctTrades()
    {
        using var store = new LensStore(FilePath); var engine = new OrderFlowEngine(store); engine.Begin(Contract, T);
        engine.Accept(Trade(), T); engine.Accept(Trade(id: 101), T); engine.Accept(Trade(), T);
        engine.Flush(T + 1000);
        Assert.Equal(2, engine.View.Latest(FlowRules.Spot)!.Messages);
        Assert.Equal(.24m, engine.View.Latest(FlowRules.Spot)!.CvdBtc);
        Assert.Single(store.CvdPoints(0));
    }
    [Fact] public void PartialOverlapOutOfOrderStaleFutureAndClosedSegmentsAreRejected()
    {
        var first = FlowRules.Apply(Segment(), null, Trade(), Contract, T);
        Assert.Throws<InvalidDataException>(() => FlowRules.Apply(first.Segment, first.Point, Trade(id: 102, count: 3), Contract, T));
        Assert.Throws<InvalidDataException>(() => FlowRules.Apply(first.Segment, first.Point, Trade(id: 101, time: T - 1), Contract, T));
        Assert.Throws<InvalidDataException>(() => FlowRules.Apply(Segment(), null, Trade(), Contract, T + 30_001));
        Assert.Throws<InvalidDataException>(() => FlowRules.Apply(Segment(), null, Trade(time: T + 5_001), Contract, T));
        Assert.Throws<ArgumentException>(() => FlowRules.Apply(Segment() with { EndedAt = T }, null, Trade(), Contract, T));
    }
    [Fact] public void ExactDecimalRawEvidenceAndTotalsPersistAndRestartStartsNewZeroSegment()
    {
        string oldId;
        using (var store = new LensStore(FilePath))
        {
            var engine = new OrderFlowEngine(store); engine.Begin(Contract, T);
            engine.Accept(Trade(size: .12345678m), T); engine.Flush(T + 1000);
            oldId = engine.View.Latest(FlowRules.Spot)!.Id;
            Assert.Equal(.12345678m, store.FindCapturedTrade(FlowRules.Spot + ":100")!.QuantityBtc);
        }
        using (var store = new LensStore(FilePath))
        {
            var engine = new OrderFlowEngine(store);
            Assert.NotNull(store.FlowSegments().Single(s => s.Id == oldId).EndedAt);
            engine.Begin(Contract, T + 2000); engine.Accept(Trade(size: .12345678m), T + 2000);
            engine.Accept(Trade(id: 101, side: "sell", size: .05m, time: T + 2100), T + 2100); engine.Flush(T + 3000);
            var current = engine.View.Latest(FlowRules.Spot)!;
            Assert.NotEqual(oldId, current.Id); Assert.Equal(-.05m, current.CvdBtc); Assert.Equal(1, current.Messages);
            Assert.Equal(.12345678m, store.CapturedTrades(oldId).Single().QuantityBtc);
        }
    }
    [Fact] public void RawTradeConflictCannotCorruptPersistedTotalsAndBatchRollsBack()
    {
        using var store = new LensStore(FilePath); var engine = new OrderFlowEngine(store); engine.Begin(Contract, T);
        engine.Accept(Trade(), T); engine.Flush(T + 1000);
        Assert.Throws<InvalidDataException>(() => engine.Accept(Trade(size: 100), T + 1100));
        var segment = engine.View.Latest(FlowRules.Spot)!;
        var original = store.FindCapturedTrade(FlowRules.Spot + ":100")!;
        Assert.Throws<ArgumentException>(() => store.SaveFlowBatch([segment with { BuyBtc = 100 }], [],
            [original with { Id = "new-raw" }, original]));
        Assert.Null(store.FindCapturedTrade("new-raw")); Assert.Equal(.12m, store.FlowSegments().Single(s => s.Id == segment.Id).CvdBtc);
    }
    [Fact] public void ExportReadsOnePersistedCheckpointEvenWhenTheRenderedSegmentIsOlder()
    {
        using var store = new LensStore(FilePath); var engine = new OrderFlowEngine(store); engine.Begin(Contract, T);
        engine.Accept(Trade(), T); engine.Flush(T + 1000);
        var rendered = engine.View.Latest(FlowRules.Spot)!;
        engine.Accept(Trade(id: 101, time: T + 1200), T + 1200); engine.Flush(T + 2000);
        var captured = store.CaptureFlow(rendered.Id)!;
        Assert.Equal(1, rendered.Messages); Assert.Equal(2, captured.Segment.Messages);
        Assert.Equal(captured.Trades.Length, captured.Segment.Messages);
        Assert.Equal(captured.Trades.Sum(t => t.QuantityBtc), captured.Segment.BuyBtc);
        engine.Accept(Trade(id: 102, time: T + 2200), T + 2200); engine.Flush(T + 3000);
        Assert.Equal(2, captured.Segment.Messages); Assert.Equal(2, captured.Trades.Length);
    }
    [Fact] public void SilenceBeyondThirtySecondsEndsEligibilityAndNextSegmentDoesNotBridgeGap()
    {
        using var store = new LensStore(FilePath); var engine = new OrderFlowEngine(store); engine.Begin(Contract, T);
        engine.Accept(Trade(), T);
        Assert.Throws<InvalidDataException>(() => engine.Accept(Trade(id: 101, time: T + 30_001), T + 30_001));
        engine.End("gap", T + 30_001); engine.Begin(Contract, T + 31_000);
        engine.Accept(Trade(id: 102, time: T + 31_100), T + 31_100); engine.Flush(T + 32_000);
        Assert.Equal(.12m, engine.View.Latest(FlowRules.Spot)!.CvdBtc);
        Assert.Equal(2, engine.View.Points.Select(p => p.SegmentId).Distinct().Count());
    }
    [Fact] public void OiUnitsAreIndependentExactAndSamplesDeduplicateAcrossRestart()
    {
        var oi = new OpenInterestSample(FlowRules.Perpetual, T, 2216113.01000000309m, 22161.1301000000309m,
            1939251795.54769270396321m, T, "REST");
        using (var store = new LensStore(FilePath))
        {
            var engine = new OrderFlowEngine(store); engine.AcceptOpenInterest(oi, T); engine.AcceptOpenInterest(oi, T);
            Assert.Throws<InvalidDataException>(() => engine.AcceptOpenInterest(oi with { Timestamp = T - 30_001 }, T));
            Assert.Throws<InvalidDataException>(() => engine.AcceptOpenInterest(oi with { CoinBtc = -1 }, T));
        }
        using (var store = new LensStore(FilePath)) Assert.Equal(oi, Assert.Single(store.OpenInterest(0)));
    }
    [Fact] public void OiFailureRemainsVisibleWhileCvdFlushesAndRecoveryClearsIt()
    {
        using var store = new LensStore(FilePath); var engine = new OrderFlowEngine(store); engine.Begin(Contract, T);
        engine.ReportOiError("OI unavailable"); engine.Accept(Trade(), T); engine.Flush(T + 1000);
        Assert.Equal("OI unavailable", engine.View.OiError);
        engine.AcceptOpenInterest(new(FlowRules.Perpetual, T + 2000, 100, 1, 67000, T + 2000, "REST"), T + 2000);
        Assert.Null(engine.View.OiError);
    }
    [Fact] public void ReportRejectsFutureAndExpiredFlowAndOnlyQuotesFreshLiveEvidence()
    {
        var segment = Segment() with { Messages = 1, BuyBtc = .1m, LastTradeAt = T };
        var oi = new OpenInterestSample(FlowRules.Perpetual, T, 100, 1, 67000, T, "REST");
        var market = MarketView.Empty with { Mode = "live", Cutoff = T,
            Flow = new("live", "test", null, [segment], [], [oi], T) };
        var fresh = BtcLens.Application.AnalysisReport.Build(market, []);
        Assert.Contains(fresh.Observations, s => s.Contains("观测成交 CVD"));
        Assert.Contains(fresh.Observations, s => s.Contains("OI 本身"));
        foreach (var cutoff in new[] { T - 1, T + 30_001 })
        {
            var report = BtcLens.Application.AnalysisReport.Build(market with { Cutoff = cutoff }, []);
            Assert.DoesNotContain(report.Observations, s => s.Contains("观测成交 CVD") || s.Contains("OI 本身"));
            Assert.Contains(report.Missing, s => s.Contains("OI"));
        }
    }
    [Fact] public void OkxParserPreservesAggregateSourceSequenceAndBothQuantityUnits()
    {
        var json = """
            {"arg":{"channel":"trades","instId":"BTC-USDT"},"data":[{"instId":"BTC-USDT","tradeId":"123","px":"67000.1","sz":"0.12060306","side":"buy","ts":"1791641000000","count":"3","source":"1","seqId":1234}]}
            """;
        var trade = Assert.Single(OkxClient.ParseTrades(json));
        Assert.Equal(.12060306m, trade.Size); Assert.Equal(3, trade.Count); Assert.Equal(1234, trade.SequenceId); Assert.Equal("1", trade.Source);
        Assert.Equal(.12060306m, FlowRules.QuantityBtc(trade, Contract));
        Assert.Throws<InvalidDataException>(() => OkxClient.ParseTrades(json.Replace("\"channel\":\"trades\"", "\"channel\":\"trades-all\"")));
        Assert.Throws<InvalidDataException>(() => OkxClient.ParseTrades(json.Replace("\"data\":[{\"instId\":\"BTC-USDT\"", "\"data\":[{\"instId\":\"BTC-USDT-SWAP\"")));
    }
    [Fact] public void DemoOrderFlowHasOnlyRevealedPrefixAndDoesNotEnterLiveTables()
    {
        var fixture = DemoMarket.Generate(); var index = DemoMarket.InitialIndex;
        var a = DemoOrderFlow.Build(fixture, index, "demo:a"); var b = DemoOrderFlow.Build(fixture, index + 1, "demo:a");
        Assert.All(a.Points, p => Assert.True(p.Timestamp <= fixture[index - 1].CloseTime));
        Assert.All(a.OpenInterest, p => Assert.True(p.Timestamp <= a.AsOf));
        var last = a.Points.Last(p => p.Instrument == FlowRules.Spot);
        Assert.Equal(last, b.Points.Single(p => p.Id == last.Id));
        using var store = new LensStore(FilePath);
        Assert.Empty(store.CvdPoints(0)); Assert.Empty(store.OpenInterest(0));
    }
    [Fact] public async Task AlertAndJournalContextCanCarryImmutableFlowEvidence()
    {
        using var store = new LensStore(FilePath);
        var market = new MarketEngine(store, new OkxClient(new HttpClient()), new OrderFlowEngine(store), useFixture: true);
        await market.StepAsync(5);
        var evidence = Assert.Single(store.Alerts(market.View.Scope)).EvidenceJson;
        using var json = JsonDocument.Parse(evidence);
        Assert.Equal("demo", json.RootElement.GetProperty("orderFlow").GetProperty("mode").GetString());
        await market.StepAsync(); Assert.Equal(evidence, store.FindAlert(Assert.Single(store.Alerts(market.View.Scope)).Id)!.EvidenceJson);
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("btclens-flow-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
