using BtcLens.Application;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

public sealed record ChartHistoryPage(Candle[] Bars,bool Exhausted);
public sealed class ChartHistoryLoader(ILensStore store,OkxClient okx)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public async Task<ChartHistoryPage> LoadAsync(string timeframe,long before,long cutoff,CancellationToken ct)
    {
        if(!Timeframes.All.Contains(timeframe) || before<=0 || before>cutoff)throw new ArgumentException("历史周期或截止时间无效。");
        var step=Timeframes.Milliseconds(timeframe);var scope="live:chart:"+timeframe;var start=Math.Max(0,before-300*step);
        await gate.WaitAsync(ct);
        try
        {
            var cache=store.ReadCandles(scope,start,before).Concat(store.ReadCandles("live",start,before))
                .Concat(timeframe=="5m"?store.ReadCandles("live:analysis",start,before):[])
                .Where(c=>c.Timeframe==timeframe && c.Instrument==FlowRules.Perpetual && c.Valid && c.Confirmed && c.CloseTime<=cutoff);
            var cached=Distinct(cache);
            if(cached.Length==300 && cached[0].OpenTime==before-300*step && cached[^1].CloseTime==before &&
                cached.Zip(cached.Skip(1)).All(p=>p.First.CloseTime==p.Second.OpenTime))return new(cached,false);
            var incoming=await okx.HistoryCandlesAsync(timeframe,before,ct);
            if(incoming.Any(c=>c.Instrument!=FlowRules.Perpetual || c.Timeframe!=timeframe || !c.Valid || !c.Confirmed || c.OpenTime<0 || c.OpenTime>=before || c.CloseTime>cutoff))
                throw new InvalidDataException("历史接口返回未收盘或不属于本次范围的数据，已停止加载。");
            var merged=Distinct(cached.Concat(incoming));ct.ThrowIfCancellationRequested();
            store.SaveCandles(scope,incoming);
            return new(merged.TakeLast(300).ToArray(),incoming.Length==0);
        }
        finally{gate.Release();}
    }
    private static Candle[] Distinct(IEnumerable<Candle> bars)
    {
        var groups=bars.GroupBy(c=>c.OpenTime).ToArray();
        if(groups.Any(g=>g.Distinct().Count()>1))throw new InvalidDataException("历史 K 线存在冲突，已停止加载。");
        return groups.Select(g=>g.First()).OrderBy(c=>c.OpenTime).ToArray();
    }
}
