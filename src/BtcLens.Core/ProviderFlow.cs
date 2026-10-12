namespace BtcLens.Core;

public sealed record ProviderMarket(string Symbol, string Exchange, string Instrument, string Quote, string VolumeUnit);
public sealed record ProviderVolumeBar(string Symbol, long OpenTime, decimal Open, decimal High, decimal Low, decimal Close,
    decimal Volume, decimal BuyVolume);
public sealed record ProviderOiBar(string Symbol, long OpenTime, decimal Close);
public sealed record FlowOiPoint(long Timestamp, decimal Btc);
public sealed record ProviderFlowView(bool Configured, string Status, string? Error, long FetchedAt,
    ProviderMarket[] Available, string[] Selected, ProviderVolumeBar[] Volumes, ProviderOiBar[] OpenInterest)
{
    public static ProviderFlowView Empty => new(false, "尚未连接 Coinalyze", null, 0, [], [], [], []);
    public bool Fresh(long cutoff) => Configured && Error is null && FetchedAt <= cutoff && cutoff - FetchedAt <= 180_000;
}
public sealed record ProviderDisplay(FlowDisplayPoint[] Points, FlowOiPoint[] OpenInterest, string[] SpotSources,
    string[] PerpetualSources, long RequestedStart, long KnownAt, int MissingMinutes);

public static class ProviderFlow
{
    public const string Version = "Coinalyze-Closed-Common-Minute-BTC/1.0";
    public static ProviderDisplay Build(ProviderFlowView view, long start, long cutoff)
    {
        start = checked((start + 59_999) / 60_000 * 60_000);
        var end = cutoff / 60_000 * 60_000;
        if (end < start || end-start > 86_400_000) throw new ArgumentException("Coinalyze 显示范围为最近 24 小时。");
        var markets = view.Available.Where(m => view.Selected.Contains(m.Symbol)).OrderBy(m => m.Symbol).ToArray();
        if(markets.Length!=view.Selected.Length || view.Selected.Distinct().Count()!=view.Selected.Length ||
            markets.Select(m=>m.Symbol).Distinct().Count()!=markets.Length || markets.Any(m=>m.VolumeUnit!="BTC" || !FlowRules.Instruments.Contains(m.Instrument)))
            return new([],[],[],[],start,view.FetchedAt,checked((int)((end-start)/60_000))*2);
        var volumes = view.Volumes.GroupBy(b => (b.Symbol,b.OpenTime)).ToDictionary(g => g.Key,g => g.ToArray());
        var oi = view.OpenInterest.GroupBy(b => (b.Symbol,b.OpenTime)).ToDictionary(g => g.Key,g => g.ToArray());
        var result = new List<FlowDisplayPoint>(); var oiResult = new List<FlowOiPoint>(); var missing=0;
        foreach (var instrument in FlowRules.Instruments)
        {
            var selected = markets.Where(m => m.Instrument == instrument && m.VolumeUnit == "BTC").ToArray();
            decimal cvd=0; long previous=-1; string group="";
            for (var time=start;time<end;time+=60_000)
            {
                var rows = selected.Select(m => volumes.GetValueOrDefault((m.Symbol,time))).ToArray();
                if (selected.Length == 0 || rows.Any(r => r is not {Length:1} || !Valid(r[0])))
                { missing++; previous=-1; continue; }
                if (previous != time-60_000) { cvd=0; group=Version+":"+instrument+":"+string.Join("+",selected.Select(m=>m.Symbol))+":"+time; }
                var delta=rows.Sum(r=>2*r![0].BuyVolume-r[0].Volume); cvd+=delta;
                result.Add(new(group,instrument,time+60_000,cvd,delta) { Volume=rows.Sum(r=>r![0].Volume) }); previous=time;
            }
        }
        var perps=markets.Where(m=>m.Instrument==FlowRules.Perpetual && m.VolumeUnit=="BTC").ToArray();
        for(var time=start;time<end;time+=60_000)
        {
            var rows=perps.Select(m=>oi.GetValueOrDefault((m.Symbol,time))).ToArray();
            if(perps.Length>0 && rows.All(r=>r is {Length:1} && r[0].Close>=0))
                oiResult.Add(new(time+60_000,rows.Sum(r=>r![0].Close)));
        }
        return new(result.OrderBy(p=>p.Timestamp).ToArray(),oiResult.ToArray(),
            markets.Where(m=>m.Instrument==FlowRules.Spot).Select(m=>m.Exchange+" · "+m.Symbol).ToArray(),
            perps.Select(m=>m.Exchange+" · "+m.Symbol).ToArray(),start,view.FetchedAt,missing);
    }
    public static bool Valid(ProviderVolumeBar b) => b.OpenTime>=0 && b.OpenTime%60_000==0 && b.Open>0 && b.Low>0 &&
        b.High>=Math.Max(b.Open,b.Close) && b.Low<=Math.Min(b.Open,b.Close) && b.Close>0 && b.High>=b.Low &&
        b.Volume>=0 && b.BuyVolume>=0 && b.BuyVolume<=b.Volume;
}
