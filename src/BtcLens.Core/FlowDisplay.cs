namespace BtcLens.Core;

public sealed record FlowDisplayPoint(string SegmentId,string Instrument,long Timestamp,decimal Cvd,decimal Delta);
public sealed record FlowDisplayResult(FlowDisplayPoint[] Points,string[] Exchanges,long RequestedStart,long? ActualStart,long KnownAt,int MissingMinutes,string Unit);
public static class FlowDisplay
{
    public const string Version="Closed-Common-Minute-CVD/1.0";
    public static FlowDisplayResult Build(FlowView flow,IEnumerable<string> exchanges,long requestedStart,string unit)
    {
        if(unit is not ("BTC" or "USDT"))throw new ArgumentException("Unsupported flow unit.");
        var selected=exchanges.Distinct().Order().ToArray();
        if(selected.Length==0 || selected.Any(x=>!FlowRules.Exchanges.Contains(x)))throw new ArgumentException("Select a supported exchange.");
        var start=checked((requestedStart+59_999)/60_000*60_000);
        var end=flow.AsOf/60_000*60_000;
        if(end<start || end-start>86_400_000)throw new ArgumentException("Flow display supports the observed last 24 hours.");
        var segments=flow.Segments.ToDictionary(s=>s.Id);
        var rows=flow.Points.Where(p=>selected.Contains(p.Exchange) && p.Minute>=start && p.Minute<end).GroupBy(p=>(p.Exchange,p.Instrument,p.Minute))
            .ToDictionary(g=>g.Key,g=>g.ToArray());
        var result=new List<FlowDisplayPoint>();long? actual=null;var missing=0;
        foreach(var instrument in FlowRules.Instruments)
        {
            decimal cvd=0;string? group=null;string? priorSignature=null;long previous=-1;
            for(var minute=start;minute<end;minute+=60_000)
            {
                var values=new List<CvdPoint>();
                foreach(var exchange in selected)
                {
                    if(!rows.TryGetValue((exchange,instrument,minute),out var matches) || matches.Length!=1)break;
                    var p=matches[0];
                    if(!segments.TryGetValue(p.SegmentId,out var s) || s.Exchange!=exchange || s.Instrument!=instrument ||
                        s.StartedAt>minute || s.LastTradeAt is not long last || last<minute+60_000 || s.EndedAt is long ended && ended<minute+60_000 ||
                        p.Timestamp<minute || p.Timestamp>=minute+60_000 || p.Timestamp>flow.AsOf || p.BuyBtc<0 || p.SellBtc<0 ||
                        unit=="USDT" && (p.BuyUsdt is null || p.SellUsdt is null || p.BuyUsdt<0 || p.SellUsdt<0))break;
                    values.Add(p);
                }
                if(values.Count!=selected.Length){missing++;previous=-1;continue;}
                var signature=string.Join("+",values.Select(p=>p.SegmentId));
                if(previous!=minute-60_000 || signature!=priorSignature)
                { cvd=0;group=instrument+":"+unit+":"+signature+":"+minute; }
                var delta=values.Sum(p=>unit=="BTC"?p.BuyBtc-p.SellBtc:p.BuyUsdt!.Value-p.SellUsdt!.Value);
                cvd+=delta;result.Add(new(group!,instrument,minute+60_000,cvd,delta));
                actual=actual is null?minute:Math.Min(actual.Value,minute);previous=minute;priorSignature=signature;
            }
        }
        return new(result.OrderBy(p=>p.Timestamp).ToArray(),selected,start,actual,flow.AsOf,missing,unit);
    }
}
