using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BtcLens.Core;

namespace BtcLens.Web.Services;

/// <summary>Public-market transport used only by Testing + OfflineFixture browser checks.</summary>
public sealed class OfflineHistoryHandler : HttpMessageHandler
{
    private int historyRequests;
    public int HistoryRequests => Volatile.Read(ref historyRequests);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var uri = request.RequestUri!.ToString(); object[] data;
        if (uri.Contains("instruments")) data = [new { instId = "BTC-USDT-SWAP", tickSz = "0.1", ctVal = "0.01", ctValCcy = "BTC" }];
        else if (uri.Contains("candles"))
        {
            var tf = Regex.Match(uri, "bar=([^&]+)").Groups[1].Value; var step = Timeframes.Milliseconds(tf);
            var cursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / step * step;
            if (uri.Contains("history-candles")) { cursor = long.Parse(Regex.Match(uri, "after=(\\d+)").Groups[1].Value); Interlocked.Increment(ref historyRequests); }
            data = Enumerable.Range(1, 300).Select(i => (object)new[] { (cursor - i * step).ToString(), "67000", "67200", "66800", "67020", "100", "1", "100", "1" }).ToArray();
        }
        else throw new InvalidDataException("Unexpected offline test request.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = "0", data }), Encoding.UTF8, "application/json") });
    }
}
