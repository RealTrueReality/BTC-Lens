using System.Net;
using System.Text;
using System.Text.Json;

namespace BtcLens.Infrastructure;

/// <summary>Registered only by Testing + OfflineFixture. Never an application data fallback.</summary>
public sealed class OfflineCoinalyzeHandler:HttpMessageHandler
{
    public const string Key="offline-coinalyze-test-key";
    public int Requests {get;private set;}
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Requests++;
        if(!request.Headers.TryGetValues("api_key",out var keys) || keys.Single()!=Key)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized){Content=new StringContent("Invalid test key")});
        var path=request.RequestUri!.AbsolutePath;
        object result;
        if(path.EndsWith("exchanges"))result=new[]{"A","B"}.Select(e=>new {code=e,name="Test "+e});
        else if(path.EndsWith("spot-markets"))result=new[]{"A","B"}.Select(e=>new {symbol="BTCUSDT."+e,exchange=e,base_asset="BTC",quote_asset="USDT",has_buy_sell_data=true});
        else if(path.EndsWith("future-markets"))result=new[]{"A","B"}.Select(e=>new {symbol="BTCUSDT_PERP."+e,exchange=e,base_asset="BTC",quote_asset="USDT",is_perpetual=true,margined="STABLE",oi_lq_vol_denominated_in="BASE_ASSET",has_ohlcv_data=true,has_buy_sell_data=true});
        else
        {
            var query=request.RequestUri.Query.TrimStart('?').Split('&').Select(q=>q.Split('=',2)).ToDictionary(q=>q[0],q=>Uri.UnescapeDataString(q[1]));
            var start=long.Parse(query["from"]);var end=long.Parse(query["to"]);
            var times=Enumerable.Range(0,checked((int)((end-start)/60+1))).Select(n=>start+n*60).ToArray();
            result=query["symbols"].Split(',').Select(symbol=>new {symbol,history=times.Select(t=>path.EndsWith("open-interest-history")?
                (object)new {t,o=1000m,h=1001m,l=999m,c=1000m+(t-start)/60*.01m}:
                new {t,o=67000m,h=67100m,l=66900m,c=67050m,v=10m,bv=7m,tx=10,btx=7})});
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(result),Encoding.UTF8,"application/json")});
    }
}
