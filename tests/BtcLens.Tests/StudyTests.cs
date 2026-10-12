using BtcLens.Core;
using BtcLens.Infrastructure;

namespace BtcLens.Tests;
public sealed class StudyTests
{
    [Fact] public void RuleCardsHaveExplicitSourcesAndDefaultToUnreviewed()
    {
        Assert.All(StudyRules.Cards,c=>{Assert.NotEmpty(c.SourceFile);Assert.Equal(64,c.SourceHash.Length);Assert.NotEmpty(c.Anchors);});
        Assert.All(RuleProfile.Default.Reviews,r=>Assert.False(r.Reviewed));StudyRules.Validate(RuleProfile.Default);
        Assert.Throws<ArgumentException>(()=>StudyRules.Validate(RuleProfile.Default with {Parameters=new(PivotSize:0)}));
    }
    [Fact] public void WorkflowKeepsRolesSeparateAndDoesNotPromoteMissingTargetToSetup()
    {
        var market=MarketReadingTests.Fixture();var reading=MarketReader.Build(market,[]);var study=StudyWorkflow.Build(market,reading,RuleProfile.Default,StudyIntent.Empty);
        Assert.Equal(6,study.Steps.Length);Assert.False(study.ExecutionAllowed);Assert.Equal("missing",study.Steps.Single(s=>s.Id=="target").State);
        Assert.Contains(study.ChartEvidence,e=>e.Id=="region");Assert.Contains(study.Warnings,w=>w.Contains("未复核"));
        Assert.All(study.ChartEvidence,e=>Assert.True(e.KnownAt<=market.Cutoff));
    }
    [Fact] public void TargetQualificationConsumptionFailureAndFutureSelectionStayDistinct()
    {
        var market=MarketReadingTests.Fixture();var plan=new StudyIntent(1,market.Cutoff-1_000_000,Bias.Bullish,110,90,"人工上方目标",true);
        Assert.Equal("observed_manual",StudyWorkflow.TargetState(market,plan,out _));
        Assert.Equal("needs_review",StudyWorkflow.TargetState(market,plan with {TargetReviewed=false},out _));
        Assert.Equal("consumed",StudyWorkflow.TargetState(market with {Quote=new(111,market.Cutoff,"test")},plan,out _));
        Assert.Equal("invalidated",StudyWorkflow.TargetState(market with {Quote=new(89,market.Cutoff,"test")},plan,out _));
        Assert.Equal("missing",StudyWorkflow.TargetState(market,plan with {ChosenAt=market.Cutoff+1},out _));
        Assert.Equal("unclear",StudyWorkflow.TargetState(market,plan with {ChosenAt=market.Cutoff-86_400_000},out _));
    }
    [Fact] public void CrossingSelectionCandleDoesNotSupplyPostSelectionIntrabarOrder()
    {
        var market=MarketReadingTests.Fixture();var last=market.Candles["5m"].Last();
        var plan=new StudyIntent(1,last.OpenTime+1000,Bias.Bullish,last.High-.1m,90,"人工候选",true);
        Assert.Equal("unclear",StudyWorkflow.TargetState(market,plan,out _));
    }
    [Fact] public void IntentRequiresFreshQuoteCorrectDirectionAndSourceReason()
    {
        var market=MarketReadingTests.Fixture();var valid=new StudyIntent(0,0,Bias.Bullish,110,90,"上方流动性候选",true);StudyRules.ValidateIntent(valid,market);
        Assert.Throws<ArgumentException>(()=>StudyRules.ValidateIntent(valid with {Target=100},market));
        Assert.Throws<ArgumentException>(()=>StudyRules.ValidateIntent(valid with {TargetReason=""},market));
        Assert.Throws<ArgumentException>(()=>StudyRules.ValidateIntent(valid,market with {Quote=market.Quote! with {Timestamp=market.Cutoff-30_001}}));
    }
    [Fact] public void ProfilesAndIntentVersionsPersistWithoutOverwritingEarlierAnalysisInputs()
    {
        var path=Path.Combine(Path.GetTempPath(),"btclens-study-"+Guid.NewGuid().ToString("N"),"test.db");
        try{using var store=new LensStore(path);var studies=new StudyStore(store);var market=MarketReadingTests.Fixture();
            var first=studies.SaveProfile(RuleProfile.Default,0,market.Cutoff);var second=studies.SaveProfile(first with {Parameters=first.Parameters with {PivotSize=3}},1,market.Cutoff+1000);
            Assert.Equal(2,second.Revision);Assert.Equal(2,first.Parameters.PivotSize);Assert.Throws<ArgumentException>(()=>studies.SaveProfile(first,1,market.Cutoff));
            var intent=studies.SaveIntent("live",new(0,0,Bias.Bullish,110,90,"上方目标",true),0,market);Assert.Equal(market.Cutoff,intent.ChosenAt);
            Assert.Equal(intent,new StudyStore(store).Intent("live"));Assert.Equal(JsonCodec.Write(second),JsonCodec.Write(new StudyStore(store).Profile));
        }finally{Directory.Delete(Path.GetDirectoryName(path)!,true);}
    }
    [Fact] public void PersonalThresholdsAreAppliedToTheSameDataWindow()
    {
        var market=MarketReadingTests.Fixture();var normal=MarketReader.Build(market,[]);var strict=MarketReader.Build(market,[],15,new(FlowPercent:45));
        Assert.Contains("现货与合约主动买入",normal.Regime);Assert.DoesNotContain("现货与合约主动买入",strict.Regime);Assert.Contains(strict.Conflicts,s=>s.Contains("45%"));
    }
    [Fact] public void EarlierInvalidationCannotBeReplacedByALaterTargetTouch()
    {
        var market=MarketReadingTests.Fixture();var bars=market.Candles["5m"];
        var chosen=bars[^3].OpenTime;var changed=bars.Select((b,i)=>i==bars.Length-2?b with {Open=100,High=101,Low=89,Close=89}:b).ToArray();
        market=market with {Candles=new Dictionary<string,Candle[]>{{"5m",changed}},Quote=new(111,market.Cutoff,"test")};
        Assert.Equal("invalidated",StudyWorkflow.TargetState(market,new(1,chosen,Bias.Bullish,110,90,"target",true),out _));
    }
    [Fact] public void ObservedTerminalIsPersistentAndCannotRestoreAfterQuoteRetreatOrRestart()
    {
        var path=Path.Combine(Path.GetTempPath(),"btclens-terminal-"+Guid.NewGuid().ToString("N"),"test.db");
        try{using var store=new LensStore(path);var studies=new StudyStore(store);var market=MarketReadingTests.Fixture();studies.SaveIntent("live",new(0,0,Bias.Bullish,110,90,"target",true),0,market);
            var touched=market with {Cutoff=market.Cutoff+1000,Quote=new(111,market.Cutoff+1000,"test")};studies.ObserveQuote(touched);
            var back=market with {Cutoff=market.Cutoff+2000,Quote=new(102,market.Cutoff+2000,"test")};
            Assert.Equal("consumed",new StudyStore(store).BuildStudy(back,[],15).Steps.Single(s=>s.Id=="target").State);
            var before=studies.BuildStudy(market,[],15);Assert.NotEqual("consumed",before.Steps.Single(s=>s.Id=="target").State);
        }finally{Directory.Delete(Path.GetDirectoryName(path)!,true);}
    }
}
