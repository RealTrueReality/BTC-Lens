using System.Net;
using System.Text.Json;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

/// <summary>Server-side API key header; each requested symbol consumes one rate-limit credit.</summary>
public sealed class CoinalyzeClient(HttpClient http)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Queue<(DateTimeOffset Time,int Cost)> calls=[];
    private DateTimeOffset retryAfter;
    private async Task<JsonDocument> Get(string path,string key,int cost,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(key))throw new ArgumentException("请先填写 Coinalyze API Key。");
        while(true)
        {
            TimeSpan delay;
            await gate.WaitAsync(ct);
            try
            {
                var now=DateTimeOffset.UtcNow;
                while(calls.TryPeek(out var call) && now-call.Time>=TimeSpan.FromSeconds(61))calls.Dequeue();
                delay=retryAfter>now?retryAfter-now:calls.Sum(c=>c.Cost)+cost>35?calls.Peek().Time.AddSeconds(61)-now:TimeSpan.Zero;
                if(delay<=TimeSpan.Zero){calls.Enqueue((now,cost));break;}
            } finally{gate.Release();}
            await Task.Delay(delay,ct);
        }
        using var request=new HttpRequestMessage(HttpMethod.Get,path);
        request.Headers.Add("api_key",key);
        using var response=await http.SendAsync(request,ct);
        if(response.StatusCode==HttpStatusCode.TooManyRequests)
        {
            await gate.WaitAsync(ct);
            try { retryAfter=response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(61)); }
            finally{gate.Release();}
        }
        if(!response.IsSuccessStatusCode)
            throw new InvalidDataException(response.StatusCode switch
            { HttpStatusCode.Unauthorized=>"Coinalyze API Key 无效或未授权。",HttpStatusCode.TooManyRequests=>"Coinalyze 达到免费限额，等待下一次刷新。",_=>$"Coinalyze 请求失败（HTTP {(int)response.StatusCode}）。" });
        // Never include response bodies or credentials in exceptions/logs.
        var json=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),cancellationToken:ct);
        if(json.RootElement.ValueKind!=JsonValueKind.Array){json.Dispose();throw new InvalidDataException("Coinalyze 返回格式无效。");}
        return json;
    }
    public async Task<ProviderMarket[]> Markets(string key,CancellationToken ct)
    {
        var result=new List<ProviderMarket>();
        using var exchanges=await Get("exchanges",key,1,ct);
        var names=exchanges.RootElement.EnumerateArray().ToDictionary(e=>Text(e,"code"),e=>Text(e,"name"));
        using(var spot=await Get("spot-markets",key,1,ct))
        foreach(var m in spot.RootElement.EnumerateArray())
            if(Text(m,"base_asset")=="BTC" && Quotes.Contains(Text(m,"quote_asset")) && Flag(m,"has_buy_sell_data"))
                result.Add(new(Text(m,"symbol"),names.GetValueOrDefault(Text(m,"exchange"),"交易所 "+Text(m,"exchange")),FlowRules.Spot,Text(m,"quote_asset"),"BTC"){ExchangeCode=Text(m,"exchange")});
        using(var futures=await Get("future-markets",key,1,ct))
        foreach(var m in futures.RootElement.EnumerateArray())
            if(Text(m,"base_asset")=="BTC" && Quotes.Contains(Text(m,"quote_asset")) && Flag(m,"is_perpetual") &&
                Text(m,"margined")=="STABLE" && Text(m,"oi_lq_vol_denominated_in")=="BASE_ASSET" &&
                Flag(m,"has_buy_sell_data") && Flag(m,"has_ohlcv_data"))
                result.Add(new(Text(m,"symbol"),names.GetValueOrDefault(Text(m,"exchange"),"交易所 "+Text(m,"exchange")),FlowRules.Perpetual,Text(m,"quote_asset"),"BTC"){ExchangeCode=Text(m,"exchange")});
        if(result.Any(m=>string.IsNullOrWhiteSpace(m.Symbol) || string.IsNullOrWhiteSpace(m.Exchange)) || result.Select(m=>m.Symbol).Distinct().Count()!=result.Count)
            throw new InvalidDataException("Coinalyze 市场元数据不完整或重复。");
        if(!result.Any(m=>m.Instrument==FlowRules.Spot) || !result.Any(m=>m.Instrument==FlowRules.Perpetual))
            throw new InvalidDataException("Coinalyze 未返回可核对单位的 BTC 现货和线性永续市场。");
        return result.OrderBy(m=>m.Instrument).ThenBy(m=>m.Exchange).ThenBy(m=>m.Symbol).ToArray();
    }
    public async Task<(ProviderVolumeBar[] Volumes,ProviderOiBar[] Oi)> History(string key,ProviderMarket[] selected,long end,CancellationToken ct)
    {
        ValidateSelection(selected);
        var start=end-86_400_000;var volumes=new List<ProviderVolumeBar>();var oi=new List<ProviderOiBar>();
        foreach(var batch in selected.Chunk(20))
        {
            using var json=await Get(Query("ohlcv-history",batch,start,end),key,batch.Length,ct);
            foreach(var market in json.RootElement.EnumerateArray())
            {
                var symbol=Text(market,"symbol");if(!batch.Any(m=>m.Symbol==symbol))throw new InvalidDataException("Coinalyze 返回未请求的市场。");
                foreach(var b in market.GetProperty("history").EnumerateArray())
                {
                    var time=checked(b.GetProperty("t").GetInt64()*1000);
                    var row=new ProviderVolumeBar(symbol,time,Number(b,"o"),Number(b,"h"),Number(b,"l"),Number(b,"c"),Number(b,"v"),Number(b,"bv"));
                    if(time<start || time>=end || !ProviderFlow.Valid(row))throw new InvalidDataException("Coinalyze 买卖量或时间单位无效。");
                    volumes.Add(row);
                }
            }
        }
        foreach(var batch in selected.Where(m=>m.Instrument==FlowRules.Perpetual).Chunk(20))
        {
            using var json=await Get(Query("open-interest-history",batch,start,end)+"&convert_to_usd=false",key,batch.Length,ct);
            foreach(var market in json.RootElement.EnumerateArray())
            {
                var symbol=Text(market,"symbol");if(!batch.Any(m=>m.Symbol==symbol))throw new InvalidDataException("Coinalyze 返回未请求的 OI 市场。");
                foreach(var b in market.GetProperty("history").EnumerateArray())
                {
                    var time=checked(b.GetProperty("t").GetInt64()*1000);var close=Number(b,"c");
                    if(time<start || time>=end || time%60_000!=0 || close<0)throw new InvalidDataException("Coinalyze OI 或时间单位无效。");
                    oi.Add(new(symbol,time,close));
                }
            }
        }
        return (volumes.ToArray(),oi.ToArray());
    }
    public static void ValidateSelection(ProviderMarket[] selected)
    {
        var cost=selected.Sum(m=>m.Instrument==FlowRules.Perpetual?2:1);
        if(selected.Select(m=>m.Symbol).Distinct().Count()!=selected.Length || !selected.Any(m=>m.Instrument==FlowRules.Spot) ||
            !selected.Any(m=>m.Instrument==FlowRules.Perpetual) || selected.Any(m=>m.VolumeUnit!="BTC") || cost>28)
            throw new ArgumentException("请各选择至少一个现货和永续市场；每次刷新最多 28 个额度（現货 1、永续 2）。");
    }
    public static string[] DefaultSelection(ProviderMarket[] markets) => FlowRules.Instruments.SelectMany(instrument=>
    {
        // Metadata support is not a promise of minute history. Start with the liquid
        // venues checked during integration; all other supported venues remain selectable.
        var candidates=markets.Where(m=>m.Instrument==instrument).GroupBy(m=>m.Exchange)
            .Select(g=>g.OrderBy(QuoteRank).First()).ToArray();
        var preferred=candidates.Where(m=>ExchangeRank(m.Exchange,instrument)<99).OrderBy(m=>ExchangeRank(m.Exchange,instrument)).ToArray();
        return preferred.Length>0?preferred:candidates.OrderBy(m=>m.Exchange).Take(6).ToArray();
    }).Select(m=>m.Symbol).ToArray();
    private static int ExchangeRank(string name,string instrument)
    {
        var names=instrument==FlowRules.Spot?new[]{"Binance","Coinbase","Bybit","Kraken","Bitfinex"}:new[]{"Binance","OKX","Bybit"};
        var i=Array.FindIndex(names,n=>name.Equals(n,StringComparison.OrdinalIgnoreCase));return i<0?99:i;
    }
    private static int QuoteRank(ProviderMarket m)=>m.Instrument==FlowRules.Spot && new[]{"Coinbase","Kraken","Bitfinex"}.Any(n=>m.Exchange.Contains(n,StringComparison.OrdinalIgnoreCase)) && m.Quote=="USD"?-1:Array.IndexOf(Quotes,m.Quote);
    private static readonly string[] Quotes=["USDT","USD","USDC"];
    private static string Query(string path,ProviderMarket[] markets,long start,long end) => path+"?symbols="+Uri.EscapeDataString(string.Join(",",markets.Select(m=>m.Symbol)))+
        $"&interval=1min&from={start/1000}&to={end/1000-60}";
    private static string Text(JsonElement e,string name)=>e.TryGetProperty(name,out var p)?p.GetString()??"":"";
    private static bool Flag(JsonElement e,string name)=>e.TryGetProperty(name,out var p) && p.ValueKind==JsonValueKind.True;
    private static decimal Number(JsonElement e,string name)=>e.GetProperty(name).GetDecimal();
}
