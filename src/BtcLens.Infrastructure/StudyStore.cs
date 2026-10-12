using BtcLens.Application;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

public sealed class StudyStore(ILensStore store)
{
    private readonly object sync=new();
    public RuleProfile Profile {get {lock(sync){var saved=store.GetSetting("study:profiles");return string.IsNullOrWhiteSpace(saved)?RuleProfile.Default:JsonCodec.Read<RuleProfile[]>(saved).Last();}}}
    public RuleProfile SaveProfile(RuleProfile draft,int expectedRevision,long now)
    {
        StudyRules.Validate(draft);
        lock(sync)
        {
            if(Profile.Revision!=expectedRevision)throw new ArgumentException("规则版本已变化，请刷新后重试。");
            var versions=store.GetSetting("study:profiles");var history=string.IsNullOrWhiteSpace(versions)?Array.Empty<RuleProfile>():JsonCodec.Read<RuleProfile[]>(versions);
            var next=draft with {Revision=expectedRevision+1,CreatedAt=now};store.SetSetting("study:profiles",JsonCodec.Write(history.Append(next)));return next;
        }
    }
    public StudyIntent Intent(string scope){lock(sync){var saved=store.GetSetting("study:intent:"+scope);return string.IsNullOrWhiteSpace(saved)?StudyIntent.Empty:JsonCodec.Read<StudyIntent[]>(saved).Last();}}
    public StudyIntent SaveIntent(string scope,StudyIntent draft,int expectedRevision,MarketView market)
    {
        if(scope!=market.Scope)throw new ArgumentException("工作区已变化，请重新定义观察路线。");StudyRules.ValidateIntent(draft,market);
        lock(sync)
        {
            if(Intent(scope).Revision!=expectedRevision)throw new ArgumentException("观察路线版本已变化，请刷新后重试。");
            var saved=store.GetSetting("study:intent:"+scope);var history=string.IsNullOrWhiteSpace(saved)?Array.Empty<StudyIntent>():JsonCodec.Read<StudyIntent[]>(saved);
            var next=draft with {Revision=expectedRevision+1,ChosenAt=market.Cutoff};store.SetSetting("study:intent:"+scope,JsonCodec.Write(history.Append(next)));return next;
        }
    }
    public StudySnapshot BuildStudy(MarketView market,WatchZone[] watches,int minutes)
    {
        lock(sync){ObserveQuote(market);var profile=Profile;var intent=Intent(market.Scope);var terminal=Terminal(market.Scope,intent.Revision);
            var study=StudyWorkflow.Build(market,MarketReader.Build(market,watches,minutes,profile.Parameters),profile,intent,terminal is not null && terminal.KnownAt<=market.Cutoff?terminal.State:null);
            var target=study.Steps.First(s=>s.Id=="target");if(target.State is "consumed" or "invalidated")RecordTerminal(market.Scope,intent.Revision,target.State,market.Cutoff,target.Detail);
            return study;}
    }
    public StudySnapshot BuildDefaultStudy(MarketView market,WatchZone[] watches,int minutes)
    {
        var study=StudyWorkflow.Build(market,MarketReader.Build(market,watches,minutes),RuleProfile.Default,StudyIntent.Empty);
        return study with {Warnings=study.Warnings.Where(w=>!w.StartsWith("个人规则卡仍有未复核项",StringComparison.Ordinal)).ToArray()};
    }
    public void ObserveQuote(MarketView market)
    {
        lock(sync){var intent=Intent(market.Scope);if(intent.Revision==0 || Terminal(market.Scope,intent.Revision) is not null || market.Mode!="live" ||
            market.Quote is not {} quote || quote.Timestamp<=intent.ChosenAt || quote.Timestamp>market.Cutoff || market.Cutoff-quote.Timestamp>30_000)return;
            var bull=intent.Bias==Bias.Bullish;
            if(intent.Bias==Bias.Neutral)return;
            if(intent.Invalidation is {} invalid && (bull?quote.Price<=invalid:quote.Price>=invalid) || intent.Target is {} target && (bull?quote.Price>=target:quote.Price<=target))
            {var state=StudyWorkflow.TargetState(market,intent,out var detail);if(state is "consumed" or "invalidated")RecordTerminal(market.Scope,intent.Revision,state,quote.Timestamp,detail);}}
    }
    private TerminalEvidence? Terminal(string scope,int revision){var json=store.GetSetting("study:terminal:"+scope+":"+revision);return string.IsNullOrWhiteSpace(json)?null:JsonCodec.Read<TerminalEvidence>(json);}
    private void RecordTerminal(string scope,int revision,string state,long time,string reason){if(Terminal(scope,revision) is null)store.SetSetting("study:terminal:"+scope+":"+revision,JsonCodec.Write(new TerminalEvidence(state,time,reason)));}
    public void SaveAnalysis(StudySnapshot study,object? narrative=null)=>store.SetSetting("study:analysis:"+study.Id,JsonCodec.Write(new {study,narrative}));
    private sealed record TerminalEvidence(string State,long KnownAt,string Reason);
}
