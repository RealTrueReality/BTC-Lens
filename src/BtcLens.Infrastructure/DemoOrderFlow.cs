using BtcLens.Core;

namespace BtcLens.Infrastructure;

public static class DemoOrderFlow
{
    public static FlowView Build(Candle[] fixture, int index, string scope)
    {
        var points = new List<CvdPoint>(); var segments = new List<FlowSegment>(); var oi = new List<OpenInterestSample>();
        var contract = new InstrumentInfo(FlowRules.Perpetual, .1m, .01m, "BTC");
        foreach (var instrument in FlowRules.Instruments)
        {
            var id = scope + ":" + instrument;
            var segment = new FlowSegment(id, instrument, fixture[0].CloseTime, fixture[0].CloseTime,
                "独立合成主动成交；仅演示界面，不能由 OHLCV 还原实盘 CVD", contract);
            for (var i = 0; i < index; i++)
            {
                var wave = (decimal)Math.Sin(i * .11 + (instrument == FlowRules.Spot ? .5 : 1.8));
                var buy = 1.5m + wave * .6m; var sell = 1.5m - wave * .6m;
                segment = segment with { BuyBtc = segment.BuyBtc + buy, SellBtc = segment.SellBtc + sell,
                    LastTradeAt = fixture[i].CloseTime, Messages = i + 1, Matches = i + 1 };
                if (i >= index - 120) points.Add(new(id + ":" + i, id, instrument, fixture[i].CloseTime,
                    fixture[i].CloseTime, segment.CvdBtc, buy, sell, 1));
            }
            segments.Add(segment);
        }
        for (var i = Math.Max(0, index - 120); i < index; i++)
        {
            var btc = 20_000m + (decimal)Math.Sin(i * .04) * 300m;
            oi.Add(new(FlowRules.Perpetual, fixture[i].CloseTime, btc / .01m, btc,
                btc * fixture[i].Close, fixture[i].CloseTime, "独立合成演示"));
        }
        return new("demo", "合成订单流 · 不代表真实成交", null, segments.ToArray(), points.ToArray(), oi.ToArray(), fixture[index - 1].CloseTime);
    }
}
