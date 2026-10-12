namespace BtcLens.Core;

public sealed record ReadingRegion(string Id,string Name,string Timeframe,Bias Bias,decimal Lower,decimal Upper,string Relation);
public sealed record ReadingWindow(long Start,long End,string Source,string[] SpotSources,string[] PerpetualSources,
    decimal? PricePercent,decimal? SpotDelta,decimal? SpotImbalance,decimal? PerpetualDelta,decimal? PerpetualImbalance,
    decimal? OiPercent,string OiSource);
public sealed record MarketReading(string Algorithm,long KnownAt,int Minutes,string Regime,string Quality,
    ReadingWindow Window,ReadingRegion[] Regions,string[] Evidence,string[] Conflicts,string[] Conditions,string[] Missing)
{
    public ReadingEvidence? Inputs {get;init;}
}
public sealed record ReadingEvidence(Candle[] Prices,FlowDisplayPoint[] Flow,FlowOiPoint[] OpenInterest);

/// <summary>Explicit reference interpretations; no fitted confidence, trade signal or hidden provider substitution.</summary>
public static class MarketReader
{
    public const string Version="Position-Flow-Reading/1.0:N2-P0.05-I5-OI0.1";
    public static MarketReading Build(MarketView market,WatchZone[] watches,int minutes=15)
    {
        if(minutes is not (15 or 60))throw new ArgumentException("分析窗口支持 15 分钟或 1 小时。");
        var end=market.Cutoff/300_000*300_000;var start=end-minutes*60_000L;
        var facts=new List<string>();var conflicts=new List<string>();var conditions=new List<string>();var missing=new List<string>();
        var source=market.ProviderFlow.Configured?"Coinalyze":"OKX + Binance 公共成交";
        string[] spotSources=[],perpSources=[];FlowDisplayPoint[] points=[];FlowOiPoint[] oi=[];
        var freshQuote=market.Mode=="live" && market.Quote is {Price:>0} q && q.Timestamp<=market.Cutoff && market.Cutoff-q.Timestamp<=30_000;
        if(!freshQuote)missing.Add("实时价格缺失、过期或为测试数据；暂停实盘方向确认。");
        var bars=(market.Candles.GetValueOrDefault("5m")??[]).Where(c=>c.Valid && c.Confirmed && c.Instrument==FlowRules.Perpetual && c.CloseTime<=end)
            .GroupBy(c=>c.OpenTime).Where(g=>g.Count()==1).Select(g=>g.Single()).OrderBy(c=>c.OpenTime).ToArray();
        var range=bars.Where(c=>c.OpenTime>=start && c.CloseTime<=end).ToArray();
        decimal? price=range.Length==minutes/5 && range.First().OpenTime==start && range.Last().CloseTime==end && Continuous(range)?
            (range.Last().Close/range.First().Open-1)*100:null;
        if(price is null)missing.Add("分析窗口的 5m 收盘价格不完整，无法比较价格与订单流。");
        if(market.ProviderFlow.Configured)
        {
            if(market.ProviderFlow.Fresh(market.Cutoff))
            {
                var display=ProviderFlow.Build(market.ProviderFlow,Math.Max(0,start-60_000),end);
                points=display.Points;oi=display.OpenInterest;spotSources=display.SpotSources;perpSources=display.PerpetualSources;
            }
            else missing.Add("已选择 Coinalyze，但历史数据未到、请求失败或超过 3 分钟未更新；暂停订单流确认。");
        }
        else if(market.Mode=="live" && market.Flow.AsOf>=start)
        {
            var display=FlowDisplay.Build(market.Flow with {AsOf=Math.Min(market.Flow.AsOf,market.Cutoff)},FlowRules.Exchanges,Math.Max(0,start),"BTC");
            points=display.Points;spotSources=FlowRules.Exchanges;perpSources=FlowRules.Exchanges;
            oi=market.Flow.OpenInterest.Where(s=>s.Valid && s.Timestamp<=end).Select(s=>new FlowOiPoint(s.Timestamp,s.CoinBtc)).ToArray();
            if(market.Flow.OpenInterest.LastOrDefault(s=>s.Timestamp<=market.Cutoff) is not {} latest || market.Cutoff-latest.Timestamp>30_000 || market.Flow.OiError is not null)
            { oi=[];missing.Add("OKX OI 当前缺失或过期。"); }
        }
        else missing.Add("公共成交仍在采集，尚无完整分析窗口。");
        var spot=Window(points,FlowRules.Spot,start,end,minutes);var perp=Window(points,FlowRules.Perpetual,start,end,minutes);
        if(spot is null)missing.Add($"现货 CVD 缺少连续且共同覆盖的 {minutes} 分钟；不能跨缺口比较累计值。");
        if(perp is null)missing.Add($"合约 CVD 缺少连续且共同覆盖的 {minutes} 分钟；不能跨缺口比较累计值。");
        var oiSource=market.ProviderFlow.Configured?"Coinalyze 所选线性永续 · BTC":"OKX BTC-USDT-SWAP · BTC";
        var oiPercent=OiChange(oi,start,end,market.ProviderFlow.Configured?0:30_000);
        if(oiPercent is null)missing.Add("同一窗口的 OI 起点、终点或中间覆盖不足。");
        facts.Add($"比较窗口 {DateTimeOffset.FromUnixTimeMilliseconds(start):O} 至 {DateTimeOffset.FromUnixTimeMilliseconds(end):O}，只使用已收盘数据；来源 {source}。");
        if(price is not null)facts.Add($"价格变化 {price:+0.00;-0.00;0.00}%。");
        if(spot is not null)facts.Add($"现货主动买卖差 {spot.Value.Delta:+0.0000;-0.0000;0.0000} BTC，差额占成交量 {spot.Value.Imbalance:+0.0;-0.0;0.0}%。");
        if(perp is not null)facts.Add($"合约主动买卖差 {perp.Value.Delta:+0.0000;-0.0000;0.0000} BTC，差额占成交量 {perp.Value.Imbalance:+0.0;-0.0;0.0}%。");
        if(oiPercent is not null)facts.Add($"OI 变化 {oiPercent:+0.00;-0.00;0.00}%（{oiSource}）；持仓变化本身无法识别多空。");
        var pd=Direction(price,.05m);var sd=Direction(spot?.Imbalance,5m);var fd=Direction(perp?.Imbalance,5m);var od=Direction(oiPercent,.1m);
        var complete=freshQuote && price is not null && spot is not null && perp is not null && oiPercent is not null;
        var regime=!complete?"证据不足，暂不能确认行情性质":pd==0?"区间波动，价格方向尚不清晰":
            sd==pd && fd==pd?pd>0?"上涨伴随现货与合约主动买入":"下跌伴随现货与合约主动卖出":
            sd!=pd && fd==pd?"合约主动成交与价格同向，现货未同步":sd==pd?"现货与价格同向，合约成交存在分歧":"价格与主动成交出现分歧";
        if(complete)
        {
            if(od>0)facts.Add("OI 增加，与净新增未平仓合约相符；是否持续推进仍要看价格结构。");
            else if(od<0)conflicts.Add($"价格{(pd>0?"上涨":pd<0?"下跌":"波动")}时 OI 下降，与净减仓相符；不能仅据此认定逼空、清算或新增趋势资金。");
            if(sd!=fd)conflicts.Add("现货与合约主动成交倾向不同，两个市场尚未形成一致确认。");
            if(pd!=0 && sd==-pd)conflicts.Add("价格与现货主动买卖差方向相反，可能存在吸收或其他交易所主导；需验证，尚不能认定吸收。");
            if(sd==0)conflicts.Add("现货主动买卖差未超过成交量的 5%，现货方向证据较弱。");
        }
        foreach(var tf in new[]{"1D","4H"})
        {
            var macro=(market.Candles.GetValueOrDefault(tf)??[]).Where(c=>c.Valid && c.Confirmed && c.CloseTime<=market.Cutoff).OrderBy(c=>c.OpenTime).TakeLast(3).ToArray();
            if(macro.Length<3 || !Continuous(macro) || market.Cutoff-macro.Last().CloseTime>=Timeframes.Milliseconds(tf))
                missing.Add($"{tf} 最近三根收盘数据不足或过期。");
            else facts.Add($"{tf} 最近两次收盘相对第一根{(macro.Skip(1).All(c=>c.Close>macro[0].Close)?"持续抬高":macro.Skip(1).All(c=>c.Close<macro[0].Close)?"持续降低":"方向混合")}；只是收盘背景，尚不等于个人结构规则确认。");
        }
        var regions=Regions(market,watches,start,freshQuote);
        foreach(var r in regions)
        {
            facts.Add($"{r.Timeframe}「{r.Name}」{r.Lower:0.0}–{r.Upper:0.0}：{r.Relation}；证据 {r.Id}。");
            if(r.Bias==Bias.Bullish && sd<0)conflicts.Add($"看涨区域「{r.Name}」附近现货仍偏主动卖出，反弹确认不足。");
            if(r.Bias==Bias.Bearish && sd>0)conflicts.Add($"看跌区域「{r.Name}」附近现货仍偏主动买入，回落确认不足。");
            conditions.Add(r.Bias switch
            {
                Bias.Bullish=>$"「{r.Name}」：观察 5m/15m 能否守住或收回 {r.Lower:0.0}，并形成向上参考结构破位；收盘持续跌破区域时撤回守住区域的判断。",
                Bias.Bearish=>$"「{r.Name}」：观察 5m/15m 能否受阻并跌回 {r.Upper:0.0} 下方，再形成向下参考结构破位；收盘持续站上区域时撤回受阻判断。",
                _=>$"「{r.Name}」：先区分价格接受区域还是回到区域内，再用同一窗口的现货 / 合约成交确认。"
            });
        }
        if(regions.Length==0)conditions.Add("价格当前没有进入可核对的关注区或 OB/FVG；先等待到区，避免把远处区域当成本次反应依据。");
        LocalReaction(bars,end,facts,missing);
        foreach(var v in market.PriceContext.Vwaps.Where(v=>v.Value is not null && v.Coverage.Complete && v.KnownAt<=market.Cutoff && market.Cutoff-v.KnownAt<600_000 && v.Anchor==PriceContextRules.Anchor(market.Cutoff,v.Period,v.OffsetHours)))
            if(freshQuote)facts.Add($"最新价格在{PriceContextRules.PeriodName(v.Period)} VWAP {v.Value:0.0} {(market.Quote!.Price>=v.Value?"上方":"下方")}（5m 近似）。");
        foreach(var p in market.PriceContext.Profiles.Where(p=>p.Visible && p.CreatedAt<=market.Cutoff && p.Result.End<=market.Cutoff && p.Result.Coverage.Complete).Take(3))
            if(freshQuote && p.Result.Val is {} val && p.Result.Vah is {} vah)facts.Add($"价格相对「{p.Name}」价值区 {(market.Quote!.Price<val?"在 VAL 下方":market.Quote.Price>vah?"在 VAH 上方":"在 VAL–VAH 内部")}，POC {p.Result.Poc:0.0}；分布为 5m 近似。");
        conditions.Add("主目标、个人有效 MSB 条件和风险预算仍待确认；这份解读不自动生成开仓、止损或止盈指令。");
        return new(Version,market.Cutoff,minutes,regime,complete?"订单流窗口完整 · 条件性解读":"数据或覆盖不足 · 等待确认",
            new(start,end,source,spotSources,perpSources,price,spot?.Delta,spot?.Imbalance,perp?.Delta,perp?.Imbalance,oiPercent,oiSource),
            regions,facts.Distinct().ToArray(),conflicts.Distinct().ToArray(),conditions.Distinct().ToArray(),missing.Distinct().ToArray())
            {Inputs=new(range,points.Where(p=>p.Timestamp>start && p.Timestamp<=end).ToArray(),oi.Where(p=>p.Timestamp>=start-30_000 && p.Timestamp<=end).ToArray())};
    }
    private static (decimal Delta,decimal Imbalance)? Window(FlowDisplayPoint[] points,string instrument,long start,long end,int minutes)
    {
        var rows=points.Where(p=>p.Instrument==instrument && p.Timestamp>start && p.Timestamp<=end).OrderBy(p=>p.Timestamp).ToArray();
        if(rows.Length!=minutes || rows.First().Timestamp!=start+60_000 || rows.Last().Timestamp!=end || rows.Select(p=>p.SegmentId).Distinct().Count()!=1 ||
            rows.Zip(rows.Skip(1)).Any(p=>p.Second.Timestamp-p.First.Timestamp!=60_000) || rows.Any(p=>p.Volume<Math.Abs(p.Delta)))return null;
        var total=rows.Sum(p=>p.Volume);return total>0?(rows.Sum(p=>p.Delta),rows.Sum(p=>p.Delta)/total*100):null;
    }
    private static decimal? OiChange(FlowOiPoint[] input,long start,long end,long tolerance)
    {
        var rows=input.Where(p=>p.Timestamp>=start-tolerance && p.Timestamp<=end && p.Btc>=0).OrderBy(p=>p.Timestamp).ToArray();
        var first=rows.LastOrDefault(p=>p.Timestamp<=start);var last=rows.LastOrDefault();
        if(first is null || last is null || first.Btc<=0 || start-first.Timestamp>tolerance || end-last.Timestamp>tolerance ||
            rows.Where(p=>p.Timestamp>=first.Timestamp).Zip(rows.Where(p=>p.Timestamp>=first.Timestamp).Skip(1)).Any(p=>p.Second.Timestamp-p.First.Timestamp>(tolerance==0?60_000:30_000)))return null;
        return (last.Btc/first.Btc-1)*100;
    }
    private static int Direction(decimal? value,decimal threshold)=>value>threshold?1:value<-threshold?-1:0;
    private static bool Continuous(Candle[] bars)=>bars.Zip(bars.Skip(1)).All(p=>p.First.CloseTime==p.Second.OpenTime);
    private static ReadingRegion[] Regions(MarketView market,WatchZone[] watches,long start,bool fresh)
    {
        if(!fresh)return [];
        var price=market.Quote!.Price;var result=new List<ReadingRegion>();
        foreach(var w in watches.Where(w=>w.Scope==market.Scope && w.Enabled && w.DeletedAt is null && w.Current.CreatedAt.ToUnixTimeMilliseconds()<=market.Cutoff))
        {
            if(WatchRules.Bounds(w.Current,market.Quote.Timestamp) is not {} b)continue;
            var relation=price>=b.Lower && price<=b.Upper?"当前在关注范围内":Distance(price,b.Lower,b.Upper)<=.002m?"接近关注范围（距离不超过 0.2%）":null;
            if(relation is not null)result.Add(new(w.Id+":v"+w.Current.Revision,w.Current.Name,w.Current.Timeframe,w.Current.Bias,b.Lower,b.Upper,relation));
        }
        foreach(var z in market.Zones.Where(z=>z.Instrument==FlowRules.Perpetual && z.KnownAt<=market.Cutoff && z.Life is ZoneLife.Active or ZoneLife.Touched)
            .Where(z=>z.Life==ZoneLife.Active || z.ChangedAt>=start && z.ChangedAt<=market.Cutoff).OrderBy(z=>z.Timeframe=="1D"?0:z.Timeframe=="4H"?1:2).ThenByDescending(z=>z.KnownAt))
        {
            var relation=price>=z.Lower && price<=z.Upper?"当前在区域内":Distance(price,z.Lower,z.Upper)<=.002m?"接近区域（距离不超过 0.2%）":null;
            if(relation is not null)result.Add(new(z.Id,z.Kind==ZoneKind.Fvg?"FVG":"Reference OB",z.Timeframe,z.Bias,z.Lower,z.Upper,relation+(z.Life==ZoneLife.Touched?"；本窗口已回访，仅用于解释反应":"；此前未回访")));
        }
        return result.Take(6).ToArray();
    }
    private static decimal Distance(decimal price,decimal lower,decimal upper)=>price<lower?(lower-price)/price:price>upper?(price-upper)/price:0;
    private static void LocalReaction(Candle[] bars,long end,List<string> facts,List<string> missing)
    {
        var recent=bars.TakeLast(26).ToArray();
        if(recent.Length<26 || recent.Last().CloseTime!=end || !Continuous(recent)){missing.Add("5m 局部反应需要最近 26 根连续收盘 K 线。");return;}
        var last=recent.Last();var previous=recent.SkipLast(1).TakeLast(20).ToArray();
        if(last.Low<previous.Min(c=>c.Low) && last.Close>previous.Min(c=>c.Low))facts.Add("最新 5m 刺穿前 20 根区间低点后收回；这是清扫 / SFP 参考候选，尚非个人规则确认。");
        if(last.High>previous.Max(c=>c.High) && last.Close<previous.Max(c=>c.High))facts.Add("最新 5m 刺穿前 20 根区间高点后跌回；这是清扫 / SFP 参考候选，尚非个人规则确认。");
        for(var p=recent.Length-4;p>=2;p--)
        {
            var around=recent.Skip(p-2).Take(5).Where((_,i)=>i!=2).ToArray();var pivot=recent[p];
            if(around.All(c=>c.High<pivot.High) && recent[^2].Close<=pivot.High && last.Close>pivot.High)
            {facts.Add($"最新 5m 收盘突破已确认 N2 摆动高点 {pivot.High:0.0}；参考向上结构破位，不能自动等同个人有效 MSB。");break;}
            if(around.All(c=>c.Low>pivot.Low) && recent[^2].Close>=pivot.Low && last.Close<pivot.Low)
            {facts.Add($"最新 5m 收盘跌破已确认 N2 摆动低点 {pivot.Low:0.0}；参考向下结构破位，不能自动等同个人有效 MSB。");break;}
        }
    }
}
