using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BtcLens.Core;
using BtcLens.Infrastructure;

namespace BtcLens.Tests;

public sealed class ChartHistoryTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"btclens-chart-history-"+Guid.NewGuid().ToString("N"));
    private string FilePath=>Path.Combine(directory,"test.db");
    private static OkxClient Client(Handler handler)=>new(new(handler){BaseAddress=new("https://example.test/")});
    [Theory][InlineData("15m")][InlineData("1H")][InlineData("1D")]
    public async Task NativePeriodsPageBackwardAndRestartReusesTheExactCache(string timeframe)
    {
        const long before=1_800_000_000_000;var handler=new Handler();
        using(var store=new LensStore(FilePath)){
            var page=await new ChartHistoryLoader(store,Client(handler)).LoadAsync(timeframe,before,before,default);
            Assert.Equal(300,page.Bars.Length);Assert.False(page.Exhausted);Assert.Equal(before-300*Timeframes.Milliseconds(timeframe),page.Bars[0].OpenTime);
            Assert.Equal(before,page.Bars[^1].CloseTime);Assert.All(page.Bars,c=>Assert.Equal(timeframe,c.Timeframe));Assert.Contains("bar="+timeframe,handler.Requests.Single());
            Assert.Equal(100,page.Bars[0].Volume);
        }
        using(var store=new LensStore(FilePath)){
            var cached=await new ChartHistoryLoader(store,Client(handler)).LoadAsync(timeframe,before,before,default);
            Assert.Equal(300,cached.Bars.Length);Assert.Single(handler.Requests);
        }
    }
    [Theory][InlineData("future")][InlineData("conflict")][InlineData("unconfirmed")]
    public async Task InvalidOrConflictingHistoryIsRejectedWithoutSaving(string problem)
    {
        using var store=new LensStore(FilePath);var handler=new Handler{Problem=problem};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ChartHistoryLoader(store,Client(handler)).LoadAsync("15m",1_800_000_000_000,1_800_000_000_000,default));
        Assert.Empty(store.ReadCandles("live:chart:15m"));
    }
    [Fact] public async Task MissingBarsStayMissingAndAnEmptyPageMarksEndOfReturnedHistory()
    {
        using var store=new LensStore(FilePath);var handler=new Handler{Problem="missing"};var loader=new ChartHistoryLoader(store,Client(handler));
        var page=await loader.LoadAsync("15m",1_800_000_000_000,1_800_000_000_000,default);Assert.Equal(299,page.Bars.Length);
        Assert.Contains(page.Bars.Zip(page.Bars.Skip(1)),p=>p.First.CloseTime!=p.Second.OpenTime);
        handler.Problem="empty";var end=await loader.LoadAsync("15m",page.Bars[0].OpenTime,1_800_000_000_000,default);Assert.True(end.Exhausted);Assert.Empty(end.Bars);
    }
    [Fact] public async Task SlowChartHistoryDoesNotBlockLiveQuotesAndCancellationDoesNotSave()
    {
        using var store=new LensStore(FilePath);using var cancel=new CancellationTokenSource();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=new Handler{Pause=async ct=>{started.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}};var market=new MarketEngine(store,Client(handler));
        var before=market.View.Cutoff-86_400_000;var loading=market.LoadChartHistoryAsync("15m",before,"live",cancel.Token);await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var time=market.View.Cutoff;await market.AcceptLiveQuoteAsync(new(67000,time,"isolated test"),default).WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(67000,market.View.Quote!.Price);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>loading);Assert.Empty(store.ReadCandles("live:chart:15m"));
    }
    private sealed class Handler:HttpMessageHandler
    {
        public string? Problem;public Func<CancellationToken,Task>? Pause;public List<string> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var uri=request.RequestUri!.ToString();Requests.Add(uri);if(Pause is not null)await Pause(ct);
            var tf=Regex.Match(uri,"bar=([^&]+)").Groups[1].Value;var before=long.Parse(Regex.Match(uri,"after=(\\d+)").Groups[1].Value);var step=Timeframes.Milliseconds(tf);
            var rows=Enumerable.Range(1,300).Where(i=>Problem!="missing" || i!=150).Select(i=>new[]{(before-i*step).ToString(),"100","110","90","105","100","1","100","1"}).ToList();
            if(Problem=="future")rows[0][0]=before.ToString();if(Problem=="unconfirmed")rows[0][8]="0";
            if(Problem=="conflict"){var extra=(string[])rows[0].Clone();extra[4]="106";rows.Add(extra);}if(Problem=="empty")rows.Clear();
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {code="0",data=rows}))};
        }
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
