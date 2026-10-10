using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

public sealed class OkxClient(HttpClient http)
{
    public async Task<InstrumentInfo> InstrumentAsync(CancellationToken ct)
    {
        using var doc = await Get("api/v5/public/instruments?instType=SWAP&instId=BTC-USDT-SWAP", ct);
        var row = doc.RootElement.GetProperty("data")[0];
        return new(row.GetProperty("instId").GetString()!, Decimal(row.GetProperty("tickSz")), Decimal(row.GetProperty("ctVal")), row.GetProperty("ctValCcy").GetString()!);
    }
    public async Task<Candle[]> CandlesAsync(string timeframe, CancellationToken ct)
    {
        if (!Timeframes.All.Append("1W").Contains(timeframe)) throw new ArgumentException("Unsupported timeframe.");
        using var doc = await Get($"api/v5/market/candles?instId=BTC-USDT-SWAP&bar={timeframe}&limit=300", ct);
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(row => new Candle("BTC-USDT-SWAP", timeframe,
            long.Parse(row[0].GetString()!, CultureInfo.InvariantCulture), Decimal(row[1]), Decimal(row[2]), Decimal(row[3]), Decimal(row[4]), Decimal(row[5]), row[8].GetString() == "1"))
            .Where(c => c.Valid).OrderBy(c => c.OpenTime).ToArray();
    }
    public async Task<PriceSample> TickerAsync(CancellationToken ct)
    {
        using var doc = await Get("api/v5/market/ticker?instId=BTC-USDT-SWAP", ct);
        var row = doc.RootElement.GetProperty("data")[0];
        return new(Decimal(row.GetProperty("last")), long.Parse(row.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture), "REST 轮询");
    }
    public async Task<OpenInterestSample> OpenInterestAsync(CancellationToken ct)
    {
        using var doc = await Get("api/v5/public/open-interest?instType=SWAP&instId=BTC-USDT-SWAP", ct);
        var row = doc.RootElement.GetProperty("data")[0];
        return new(row.GetProperty("instId").GetString()!, Long(row.GetProperty("ts")),
            Decimal(row.GetProperty("oi")), Decimal(row.GetProperty("oiCcy")), Decimal(row.GetProperty("oiUsd")),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "REST · 10 秒采样");
    }
    public static MarketTrade[] ParseTrades(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var rows)) return [];
        var arg = doc.RootElement.GetProperty("arg");
        if (arg.GetProperty("channel").GetString() != "trades") throw new InvalidDataException("Unexpected trade channel.");
        var instrument = arg.GetProperty("instId").GetString();
        return rows.EnumerateArray().Select(row =>
        {
            if (row.GetProperty("instId").GetString() != instrument) throw new InvalidDataException("Trade instrument mismatch.");
            return new MarketTrade(instrument!, Long(row.GetProperty("tradeId")), Decimal(row.GetProperty("px")),
                Decimal(row.GetProperty("sz")), row.GetProperty("side").GetString()!, Long(row.GetProperty("ts")),
                row.TryGetProperty("count", out var count) ? checked((int)Long(count)) : 1,
                row.TryGetProperty("source", out var source) ? source.GetString()! : "0",
                row.TryGetProperty("seqId", out var seq) ? Long(seq) : null);
        }).ToArray();
    }
    public static async IAsyncEnumerable<TradeStreamMessage> TradesStreamAsync(Uri endpoint,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(endpoint, ct);
        var args = FlowRules.Instruments.Select(i => new { channel = "trades", instId = i });
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { op = "subscribe", args })), WebSocketMessageType.Text, true, ct);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(socket, heartbeatCts.Token);
        var confirmed = new HashSet<string>(); var buffered = new List<MarketTrade>(); var ready = false;
        try
        {
            var buffer = new byte[32_768];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream(); WebSocketReceiveResult result;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) yield break;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 1_048_576) throw new InvalidDataException("Oversize OKX trades message.");
                } while (!result.EndOfMessage);
                var text = Encoding.UTF8.GetString(message.ToArray());
                if (text == "pong") continue;
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("event", out var eventName))
                {
                    if (eventName.GetString() == "error") throw new HttpRequestException("OKX trades subscription rejected.");
                    if (eventName.GetString() == "subscribe") confirmed.Add(doc.RootElement.GetProperty("arg").GetProperty("instId").GetString()!);
                    if (!ready && FlowRules.Instruments.All(confirmed.Contains))
                    {
                        ready = true;
                        yield return new(true, buffered.OrderBy(x => x.Timestamp).ThenBy(x => x.TradeId).ToArray());
                        buffered.Clear();
                    }
                    continue;
                }
                var trades = ParseTrades(text);
                if (ready) yield return new(false, trades);
                else { buffered.AddRange(trades); if (buffered.Count > 10_000) throw new InvalidDataException("Trade subscriptions did not complete."); }
            }
        }
        finally { heartbeatCts.Cancel(); socket.Abort(); try { await heartbeat; } catch (OperationCanceledException) { } }
    }
    private async Task<JsonDocument> Get(string relative, CancellationToken ct)
    {
        using var response = await http.GetAsync(relative, ct); response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.GetProperty("code").GetString() != "0")
        { var message = doc.RootElement.GetProperty("msg").GetString(); doc.Dispose(); throw new HttpRequestException("OKX: " + message); }
        if (doc.RootElement.GetProperty("data").GetArrayLength() == 0) { doc.Dispose(); throw new HttpRequestException("OKX returned no data."); }
        return doc;
    }
    public static async IAsyncEnumerable<PriceSample> StreamAsync(Uri endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(endpoint, ct);
        var subscribe = Encoding.UTF8.GetBytes("{\"op\":\"subscribe\",\"args\":[{\"channel\":\"tickers\",\"instId\":\"BTC-USDT-SWAP\"}]}");
        await socket.SendAsync(subscribe, WebSocketMessageType.Text, true, ct);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(socket, heartbeatCts.Token);
        try
        {
            var buffer = new byte[16_384];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream(); WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) yield break;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 131_072) throw new InvalidDataException("Oversize OKX message.");
                } while (!result.EndOfMessage);
                var text = Encoding.UTF8.GetString(message.ToArray());
                if (text == "pong") continue;
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("event", out var eventName) && eventName.GetString() == "error") throw new HttpRequestException("OKX WebSocket subscription rejected.");
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                foreach (var row in data.EnumerateArray())
                    yield return new(Decimal(row.GetProperty("last")), long.Parse(row.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture), "WebSocket");
            }
        }
        finally { heartbeatCts.Cancel(); socket.Abort(); try { await heartbeat; } catch (OperationCanceledException) { } }
    }
    private static async Task Heartbeat(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(ct))
                await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, ct);
        }
        catch (WebSocketException) { socket.Abort(); }
    }
    private static decimal Decimal(JsonElement element) => decimal.Parse(element.GetString()!, CultureInfo.InvariantCulture);
    private static long Long(JsonElement element) => element.ValueKind == JsonValueKind.String ? long.Parse(element.GetString()!, CultureInfo.InvariantCulture) : element.GetInt64();
}

public sealed record TradeStreamMessage(bool Ready, MarketTrade[] Trades);
