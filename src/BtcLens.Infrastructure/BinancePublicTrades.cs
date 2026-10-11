using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BtcLens.Core;
namespace BtcLens.Infrastructure;

public static class BinancePublicTrades
{
    public const string SpotUrl = "wss://data-stream.binance.vision/ws/btcusdt@aggTrade";
    public const string PerpetualUrl = "wss://fstream.binance.com/market/ws/btcusdt@aggTrade";
    public static MarketTrade? Parse(string json, string instrument)
    {
        if(!FlowRules.Instruments.Contains(instrument)) throw new ArgumentException("Unsupported Binance market.");
        using var document=JsonDocument.Parse(json);
        var row=document.RootElement;
        if(row.TryGetProperty("data",out var inner)) row=inner;
        if(row.TryGetProperty("code",out _) || row.TryGetProperty("e",out var e) && e.GetString()=="serverShutdown")
            throw new InvalidDataException("Binance public stream stopped or was rejected.");
        if(!row.TryGetProperty("e",out e) || e.GetString()!="aggTrade") return null;
        if(row.GetProperty("s").GetString()!="BTCUSDT") throw new InvalidDataException("Binance trade symbol mismatch.");
        var first=row.GetProperty("f").GetInt64(); var last=row.GetProperty("l").GetInt64();
        if(first<=0 || last<first) throw new InvalidDataException("Invalid Binance trade range.");
        return new(instrument,last,decimal.Parse(row.GetProperty("p").GetString()!,CultureInfo.InvariantCulture),
            decimal.Parse(row.GetProperty("q").GetString()!,CultureInfo.InvariantCulture),
            row.GetProperty("m").GetBoolean()?"sell":"buy",row.GetProperty("T").GetInt64(),checked((int)(last-first+1)),
            "aggTrade",null) {Exchange=FlowRules.Binance,AggregateId=row.GetProperty("a").GetInt64(),RawJson=row.GetRawText()};
    }
    public static async IAsyncEnumerable<MarketTrade> Stream(string instrument, string? proxyUrl,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var socket=new ClientWebSocket();
        socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout=TimeSpan.FromSeconds(15);
        if(!string.IsNullOrWhiteSpace(proxyUrl)) socket.Options.Proxy=new WebProxy(new Uri(proxyUrl));
        using var connect=CancellationTokenSource.CreateLinkedTokenSource(ct); connect.CancelAfter(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri(instrument==FlowRules.Spot?SpotUrl:PerpetualUrl),connect.Token);
        var buffer=new byte[32768];
        while(!ct.IsCancellationRequested && socket.State==WebSocketState.Open)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var message=new MemoryStream(); WebSocketReceiveResult result;
            do
            {
                result=await socket.ReceiveAsync(buffer,timeout.Token);
                if(result.MessageType==WebSocketMessageType.Close) yield break;
                if(result.MessageType!=WebSocketMessageType.Text) throw new InvalidDataException("Unexpected Binance message type.");
                message.Write(buffer,0,result.Count);
                if(message.Length>131072) throw new InvalidDataException("Oversize Binance public message.");
            } while(!result.EndOfMessage);
            if(Parse(Encoding.UTF8.GetString(message.ToArray()),instrument) is { } trade) yield return trade;
        }
    }
}
