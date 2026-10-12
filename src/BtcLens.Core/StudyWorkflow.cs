namespace BtcLens.Core;

public sealed record ChartEvidence(string Id,string Kind,string Label,string Timeframe,long Start,long KnownAt,decimal Lower,decimal Upper,Bias Bias,string Status);
public sealed record StudyStep(string Id,string Label,string State,string Detail,string[] EvidenceIds);
public sealed record StudySnapshot(string Id,string Algorithm,long KnownAt,MarketReading Reading,RuleProfile Profile,StudyIntent Intent,
    StudyStep[] Steps,ChartEvidence[] ChartEvidence,string[] Warnings,bool ExecutionAllowed=false);

public static class StudyWorkflow
{
    public const string Version="Lecture034-Study-Gates/1.0";
    public static StudySnapshot Build(MarketView market,MarketReading reading,RuleProfile profile,StudyIntent intent,string? previousTerminal=null)
    {
        StudyRules.Validate(profile);var warnings=new List<string>();var evidence=new List<ChartEvidence>();
        foreach(var r in reading.Regions)
        {
            var z=market.Zones.FirstOrDefault(z=>z.Id==r.Id);
            var watch=market.Mode=="live" && r.Id.Contains(":v");
            evidence.Add(new(r.Id,watch?"watch":"zone",r.Name,r.Timeframe,z?.SourceTime??reading.Window.Start,z?.KnownAt??reading.KnownAt,r.Lower,r.Upper,r.Bias,r.Relation));
        }
        var tf=profile.Parameters.LowerTimeframe;
        var bars=(market.Candles.GetValueOrDefault(tf)??[]).Where(c=>c.Valid && c.Confirmed && c.CloseTime<=market.Cutoff && c.Instrument==FlowRules.Perpetual)
            .GroupBy(c=>c.OpenTime).Where(g=>g.Count()==1).Select(g=>g.Single()).OrderBy(c=>c.OpenTime).TakeLast(200).ToArray();
        var segmentStart=bars.Length-1;while(segmentStart>0 && bars[segmentStart-1].CloseTime==bars[segmentStart].OpenTime)segmentStart--;
        if(segmentStart>0)bars=bars[segmentStart..];
        ChartEvidence? sweep=null,broken=null,fvg=null;bool delivery=false;
        var p=profile.Parameters.PivotSize;var last=bars.LastOrDefault();
        var continuous=bars.Length>=Math.Max(profile.Parameters.SweepLookback+1,p*2+4) && bars.Zip(bars.Skip(1)).All(pair=>pair.First.CloseTime==pair.Second.OpenTime) &&
            last is not null && market.Cutoff-last.CloseTime<Timeframes.Milliseconds(tf);
        if(continuous)
        {
            var prior=bars.SkipLast(1).TakeLast(profile.Parameters.SweepLookback).ToArray();
            var high=prior.Max(c=>c.High);var low=prior.Min(c=>c.Low);
            if(last!.Low<low && last.Close>low)sweep=new("sweep:"+tf+":"+last.OpenTime+":up","sweep","区间低点回收候选",tf,prior.First().OpenTime,last.CloseTime,low,last.Low,Bias.Bullish,"参考候选；重要摆动资格未确认");
            if(last.High>high && last.Close<high)sweep=new("sweep:"+tf+":"+last.OpenTime+":down","sweep","区间高点回收候选",tf,prior.First().OpenTime,last.CloseTime,high,last.High,Bias.Bearish,"参考候选；重要摆动资格未确认");
            // Review the latest three close events, confirming pivots before each event, never after it.
            for(var i=Math.Max(p*2+2,bars.Length-3);i<bars.Length;i++)
            {
                for(var k=i-p-1;k>=p;k--)
                {
                    var around=bars.Skip(k-p).Take(p*2+1).Where((_,n)=>n!=p).ToArray();var pivot=bars[k];
                    var bias=around.All(c=>c.High<pivot.High) && bars[i-1].Close<=pivot.High && bars[i].Close>pivot.High?Bias.Bullish:
                        around.All(c=>c.Low>pivot.Low) && bars[i-1].Close>=pivot.Low && bars[i].Close<pivot.Low?Bias.Bearish:Bias.Neutral;
                    if(bias==Bias.Neutral)continue;
                    var level=bias==Bias.Bullish?pivot.High:pivot.Low;
                    broken=new("break:"+tf+":"+bars[i].OpenTime+":"+bias,"structure","参考结构破位 N"+p,tf,pivot.OpenTime,bars[i].CloseTime,level,level,bias,"参考结构点；制造重要极值的资格待确认");
                    var ranges=Enumerable.Range(Math.Max(1,i-14),Math.Min(14,i)).Select(n=>Math.Max(bars[n].High-bars[n].Low,Math.Max(Math.Abs(bars[n].High-bars[n-1].Close),Math.Abs(bars[n].Low-bars[n-1].Close)))).ToArray();
                    var body=Math.Abs(bars[i].Close-bars[i].Open);var span=bars[i].High-bars[i].Low;
                    delivery=ranges.Length==14 && body>=ranges.Average()*profile.Parameters.BodyAtr && span>0 && body/span>=profile.Parameters.BodyFraction;
                    fvg=market.Zones.Where(z=>z.Timeframe==tf && z.Kind==ZoneKind.Fvg && z.Bias==bias && z.KnownAt==bars[i].CloseTime && z.KnownAt<=market.Cutoff && z.Life is ZoneLife.Active or ZoneLife.Touched)
                        .Select(z=>new ChartEvidence(z.Id,"zone","破位时新 FVG",tf,z.SourceTime,z.KnownAt,z.Lower,z.Upper,z.Bias,"位移证据；不自动作为目标")).FirstOrDefault();
                    // A prior close-back sweep in the same three-bar review window must precede the break.
                    for(var s=Math.Max(profile.Parameters.SweepLookback,i-3);s<i;s++)
                    {
                        var history=bars.Take(s).TakeLast(profile.Parameters.SweepLookback).ToArray();var anchor=bias==Bias.Bullish?history.Min(c=>c.Low):history.Max(c=>c.High);
                        if(bias==Bias.Bullish?bars[s].Low<anchor && bars[s].Close>anchor:bars[s].High>anchor && bars[s].Close<anchor)
                            sweep=new("sweep:"+tf+":"+bars[s].OpenTime+":"+bias,"sweep","破位前区间回收候选",tf,history.First().OpenTime,bars[s].CloseTime,
                                Math.Min(anchor,bias==Bias.Bullish?bars[s].Low:bars[s].High),Math.Max(anchor,bias==Bias.Bullish?bars[s].Low:bars[s].High),bias,"参考序列；重要流动性资格待确认");
                    }
                    break;
                }
            }
        }
        else warnings.Add($"{tf} 连续收盘历史不足或过期，局部候选暂停。");
        if(sweep is not null)evidence.Add(sweep with {Lower=Math.Min(sweep.Lower,sweep.Upper),Upper=Math.Max(sweep.Lower,sweep.Upper)});
        if(broken is not null)evidence.Add(broken);if(fvg is not null)evidence.Add(fvg);
        var context=reading.Evidence.Where(s=>s.StartsWith("1D ") || s.StartsWith("4H ")).ToArray();
        var same=sweep is not null && broken is not null && sweep.Bias==broken.Bias && sweep.KnownAt<broken.KnownAt;
        var confirmation=broken is null?"waiting":same && delivery && fvg is not null?"supported_candidate":"unsupported_candidate";
        var targetState=TargetState(market,intent,out var targetMessage);
        if(previousTerminal is "consumed" or "invalidated"){targetState=previousTerminal;targetMessage=previousTerminal=="consumed"?"此版本此前已观测到目标消费，价格回撤不恢复旧路线。":"此版本此前已观测越过失效参考，未触及目标不恢复旧路线。";}
        if(intent.Target is {} target && intent.ChosenAt<=market.Cutoff)
            evidence.Add(new("intent:"+intent.Revision+":target","target","人工观察目标",tf,intent.ChosenAt,intent.ChosenAt,target,target,intent.Bias,targetMessage));
        if(intent.Invalidation is {} invalid && intent.ChosenAt<=market.Cutoff)
            evidence.Add(new("intent:"+intent.Revision+":invalidation","invalidation","人工失效参考",tf,intent.ChosenAt,intent.ChosenAt,invalid,invalid,intent.Bias,"人工定义；不等于委托止损"));
        if(broken is not null && intent.Target is not null && intent.ChosenAt>broken.KnownAt)warnings.Add("目标选择晚于本窗口结构候选，不能将它补记为该候选的事前主目标。");
        if(evidence.Where(e=>e.Kind is "zone" or "watch").Select(e=>e.Bias).Distinct().Count(b=>b!=Bias.Neutral)>1)warnings.Add("附近同时存在相反方向区域，需要人工区分反应区、目标和背景角色。");
        if(profile.Reviews.Any(r=>!r.Reviewed))warnings.Add("个人规则卡仍有未复核项；参数仅生成参考候选，不升级为个人有效 setup。");
        warnings.Add("L001 的重要结构点、L034 的强弱阈值与目标资格尚未由自动检测证明；保留人工判断。");
        var steps=new[]
        {
            new StudyStep("context","高周期背景",context.Length==2?"observed":"missing",context.Length==2?string.Join(" ",context):"日线/4H 背景不足。",[]),
            new StudyStep("poi","反应区",reading.Regions.Length>0?"observed":"waiting",reading.Regions.Length>0?"附近区域已列出，角色仍须区分。":"等待进入 OB/FVG 或人工关注区。",reading.Regions.Select(r=>r.Id).ToArray()),
            new StudyStep("liquidity","流动性事件",sweep is null?"waiting":"reference_candidate",sweep is null?"尚无最近区间回收候选。":"已观察回收形态；不自动认定重要流动性清扫。",sweep is null?[]:[sweep.Id]),
            new StudyStep("confirmation","结构与位移",confirmation,broken is null?"等待收盘结构候选。":$"破位前清扫：{(same?"已观察":"未观察")}；参考实体位移：{(delivery?"符合阈值":"尚不符合")}；破位时新 FVG：{(fvg is not null?"已形成":"未见")}。仅作参考质量比较。",new[]{broken?.Id,fvg?.Id}.Where(id=>id is not null).Cast<string>().ToArray()),
            new StudyStep("flow","现货 / 合约 / OI",reading.Quality.StartsWith("订单流窗口完整")?"observed":"missing",reading.Regime,["flow-window"]),
            new StudyStep("target","目标与失效",targetState,targetMessage+" "+(intent.Invalidation is null?"失效参考未定义。":"失效参考由人工定义。"),intent.Target is null?[]:["intent:"+intent.Revision+":target"])
        };
        return new(Guid.NewGuid().ToString("N"),Version,market.Cutoff,reading,profile,intent,steps,evidence.DistinctBy(e=>e.Id).ToArray(),warnings.ToArray());
    }
    public static string TargetState(MarketView market,StudyIntent intent,out string message)
    {
        if(intent.Target is null || intent.ChosenAt>market.Cutoff){message="尚无有效的事前人工观察目标。";return "missing";}
        var target=intent.Target.Value;var bullish=intent.Bias==Bias.Bullish;
        var quote=market.Quote;
        var bars=market.ContextCandles.Concat(market.Candles.GetValueOrDefault("5m")??[]).Where(c=>c.Timeframe=="5m" && c.Instrument==FlowRules.Perpetual && c.Valid && c.Confirmed && c.CloseTime<=market.Cutoff)
            .GroupBy(c=>c.OpenTime).Select(g=>g.Last()).OrderBy(c=>c.OpenTime).ToArray();
        var events=new List<(long Time,string State)>();
        foreach(var bar in bars.Where(c=>c.OpenTime>=intent.ChosenAt))
        {
            if(bullish?bar.High>=target:bar.Low<=target)events.Add((bar.CloseTime,"consumed"));
            if(intent.Invalidation is {} invalid && (bullish?bar.Close<invalid:bar.Close>invalid))events.Add((bar.CloseTime,"invalidated"));
        }
        if(quote is not null && quote.Timestamp>intent.ChosenAt && quote.Timestamp<=market.Cutoff && market.Cutoff-quote.Timestamp<=30_000)
        {
            if(bullish?quote.Price>=target:quote.Price<=target)events.Add((quote.Timestamp,"consumed"));
            if(intent.Invalidation is {} invalid && (bullish?quote.Price<=invalid:quote.Price>=invalid))events.Add((quote.Timestamp,"invalidated"));
        }
        if(events.Count>0)
        {
            var first=events.Min(e=>e.Time);var states=events.Where(e=>e.Time==first).Select(e=>e.State).Distinct().ToArray();
            if(states.Length>1){message="同一收盘记录同时覆盖目标与失效，事件顺序未定，不能自动选择路线结果。";return "unclear";}
            message=states[0]=="consumed"?"选择后已观测到目标覆盖，原观察路线需重新审视。":"选择后已越过人工失效参考，未触及目标不能恢复旧路线。";
            return states[0];
        }
        if(bars.Any(c=>c.OpenTime<intent.ChosenAt && c.CloseTime>intent.ChosenAt && (bullish?c.High>=target:c.Low<=target))){message="跨选择时间的 K 线覆盖目标，无法仅凭该 K 线确定选择后触及顺序。";return "unclear";}
        var start=(intent.ChosenAt+299_999)/300_000*300_000;var end=market.Cutoff/300_000*300_000;
        var required=bars.Where(c=>c.OpenTime>=start && c.CloseTime<=end).ToArray();
        if(end>start && (required.LongLength!=(end-start)/300_000 || required.Zip(required.Skip(1)).Any(p=>p.First.CloseTime!=p.Second.OpenTime))){message="目标选择后的历史有缺口，不能认定目标仍开放。";return "unclear";}
        if(!intent.TargetReviewed){message="目标角色及来源说明尚未人工确认。";return "needs_review";}
        message="人工观察目标暂未见选择后消费证据；不自动成为课程 Primary Target。";return "observed_manual";
    }
}
