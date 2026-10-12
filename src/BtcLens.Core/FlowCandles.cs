namespace BtcLens.Core;

public sealed record IndicatorCandle(string Group,int Pane,long OpenTime,long KnownAt,decimal Open,decimal High,decimal Low,decimal Close,string Basis);
public static class FlowCandles
{
    public const string Sampled="minute_samples",Observed="observed_trades",OiSampled="oi_samples";
    public static IndicatorCandle[] Cvd(FlowDisplayPoint[] points,int pane,int minutes)
    {
        if(minutes is not (1 or 5 or 15))throw new ArgumentException("Unsupported indicator interval.");
        var period=minutes*60_000L;var result=new List<IndicatorCandle>();
        foreach(var group in points.GroupBy(p=>p.SegmentId))
        foreach(var bucket in group.GroupBy(p=>(p.Timestamp-1)/period*period))
        {
            var rows=bucket.OrderBy(p=>p.Timestamp).ToArray();var start=bucket.Key;
            if(rows.Length!=minutes || rows.First().Timestamp!=start+60_000 || rows.Last().Timestamp!=start+period ||
                rows.Zip(rows.Skip(1)).Any(p=>p.Second.Timestamp-p.First.Timestamp!=60_000))continue;
            var exact=rows.All(p=>p.Open is not null && p.High is not null && p.Low is not null);
            if(minutes==1 && !exact)continue;
            var open=exact?rows.First().Open!.Value:rows.First().Cvd-rows.First().Delta;
            var high=exact?rows.Max(p=>p.High!.Value):Math.Max(open,rows.Max(p=>p.Cvd));
            var low=exact?rows.Min(p=>p.Low!.Value):Math.Min(open,rows.Min(p=>p.Cvd));
            var close=rows.Last().Cvd;
            if(high<Math.Max(open,close) || low>Math.Min(open,close) || high<low)continue;
            result.Add(new(group.Key,pane,start,start+period,open,high,low,close,exact?Observed:Sampled));
        }
        return result.OrderBy(b=>b.OpenTime).ToArray();
    }
    public static IndicatorCandle[] Oi(FlowOiPoint[] input,int minutes,long maxGap)
    {
        if(minutes is not (1 or 5 or 15))throw new ArgumentException("Unsupported indicator interval.");
        var period=minutes*60_000L;var result=new List<IndicatorCandle>();
        var rows=input.OrderBy(p=>p.Timestamp).ToArray();var group=0;var segments=new List<(int Group,FlowOiPoint Row)>();
        for(var i=0;i<rows.Length;i++){if(i>0 && rows[i].Timestamp-rows[i-1].Timestamp>maxGap)group++;segments.Add((group,rows[i]));}
        foreach(var segment in segments.GroupBy(x=>x.Group))
        foreach(var bucket in segment.GroupBy(x=>(x.Row.Timestamp-1)/period*period))
        {
            var samples=bucket.OrderBy(x=>x.Row.Timestamp).Select(x=>x.Row).ToArray();var start=bucket.Key;
            var prior=segment.Select(x=>x.Row).LastOrDefault(p=>p.Timestamp<=start);
            if(prior is null || start-prior.Timestamp>maxGap || samples.Length==0 || samples.Last().Timestamp<start+period-maxGap ||
                samples.Last().Timestamp>start+period)continue;
            if(maxGap==60_000 && (prior.Timestamp!=start || samples.Last().Timestamp!=start+period))continue;
            var values=samples.Select(p=>p.Btc).Prepend(prior.Btc).ToArray();
            result.Add(new("oi:"+segment.Key,3,start,samples.Last().Timestamp,values.First(),values.Max(),values.Min(),values.Last(),OiSampled));
        }
        return result.OrderBy(b=>b.OpenTime).ToArray();
    }
}
