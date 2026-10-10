using BtcLens.Core;
using BtcLens.Application;

namespace BtcLens.Tests;
public sealed class TradeTests
{
    public static Trade Example() => new() { Mode="live", Side=TradeSide.Long,
        EntryTime = new DateTimeOffset(2026,10,1,9,0,0,TimeSpan.FromHours(8)), ExitTime = new DateTimeOffset(2026,10,1,10,0,0,TimeSpan.FromHours(8)),
        EntryPrice=67000, ExitPrice=67300, QuantityBtc=.01m, InitialStop=66900, Fees=.6m, Funding=-.1m };
    [Fact] public void LongNetPnlIncludesCostsAndSignedFunding()
    { var r=TradeMath.Calculate(Example()); Assert.Equal(3m,r.Gross); Assert.Equal(2.3m,r.Net); Assert.Equal(1m,r.InitialRisk); Assert.Equal(2.3m,r.NetR); }
    [Fact] public void ShortDirectionIsCorrect()
    { var r=TradeMath.Calculate(Example() with {Side=TradeSide.Short,ExitPrice=66700,InitialStop=67100}); Assert.Equal(3m,r.Gross); Assert.Equal(2.3m,r.Net); }
    [Theory] [InlineData(true)] [InlineData(false)] public void UnknownCostDoesNotBecomeZero(bool fee)
    { var t=fee ? Example() with {Fees=null} : Example() with {Funding=null}; var r=TradeMath.Calculate(t); Assert.Equal(3m,r.Gross); Assert.Null(r.Net); Assert.Null(r.NetR); }
    [Fact] public void MissingStopLeavesRUnknown() { Assert.Null(TradeMath.Calculate(Example() with {InitialStop=null}).NetR); }
    [Theory] [InlineData(67000)] [InlineData(68000)] public void WrongInitialStopIsRejected(int stop) { Assert.Throws<ArgumentException>(()=>TradeMath.Validate(Example() with {InitialStop=stop})); }
    [Fact] public void MissingExitTimeIsRejected() { Assert.Throws<ArgumentException>(()=>TradeMath.Validate(Example() with {ExitTime=null})); }
    [Fact] public void StatsExcludeIncompleteCostsAndCountBreakeven()
    {
        var t=Example(); var s=TradeMath.Summarize([t,t with {Fees=null},t with {ExitPrice=67000,Fees=0,Funding=0},t with {ExitPrice=66900}]);
        Assert.Equal(4,s.Closed); Assert.Equal(3,s.CostComplete); Assert.Equal(1,s.Wins); Assert.Equal(1,s.Losses); Assert.Equal(1,s.Breakeven);
        Assert.Equal(1m/3,s.WinRate);
    }
    [Fact] public void CsvParsesQuotedFieldsAndStableId()
    {
        var text=TradeCsv.Header+"\nref-1,Long,2026-10-01T09:00:00+08:00,2026-10-01T10:00:00+08:00,67000,67300,0.01,66900,67300,0.6,0,\"reason, with comma\",review,planned\n";
        var p=TradeCsv.Preview(text,"live"); Assert.Empty(p.Errors); var t=Assert.Single(p.Trades); Assert.Equal("reason, with comma",t.Reason);
        Assert.Equal(t.Id,Assert.Single(TradeCsv.Preview(text,"live").Trades).Id);
        Assert.NotEqual(t.Id,Assert.Single(TradeCsv.Preview(text,"demo").Trades).Id);
    }
    [Fact] public void CsvRejectsAmbiguousTimezoneAndDuplicateExternalId()
    {
        var text=TradeCsv.Template.Replace("T09:00:00+08:00","T09:00:00"); Assert.NotEmpty(TradeCsv.Preview(text,"live").Errors);
        var repeated=TradeCsv.Template+TradeCsv.Template.Split('\n')[1]+"\n"; Assert.NotEmpty(TradeCsv.Preview(repeated,"live").Errors);
    }
}
