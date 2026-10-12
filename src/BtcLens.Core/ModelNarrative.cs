namespace BtcLens.Core;

public sealed record NarrativeStatement(string Text,string[] EvidenceIds);
public sealed record ModelNarrative(string Summary,NarrativeStatement[] Support,NarrativeStatement[] Conflicts,NarrativeStatement[] Watch,
    string Model,string SnapshotId,long GeneratedAt,int InputTokens,int OutputTokens);
public static class NarrativeRules
{
    public static string[] References(StudySnapshot study)=>new[]{"flow-window","price-window","macro-context","target-state"}
        .Concat(study.ChartEvidence.Select(e=>e.Id)).Concat(StudyRules.Cards.Select(c=>"rule:"+c.Id)).Distinct().ToArray();
    public static void Validate(ModelNarrative narrative,StudySnapshot study)
    {
        if(narrative.SnapshotId!=study.Id || string.IsNullOrWhiteSpace(narrative.Summary) || narrative.Summary.Length>1600 ||
            narrative.Support.Length>8 || narrative.Conflicts.Length>8 || narrative.Watch.Length>8)throw new InvalidDataException("模型解释缺少内容、版本不匹配或超过长度限制。");
        var allowed=References(study).ToHashSet();
        foreach(var item in narrative.Support.Concat(narrative.Conflicts).Concat(narrative.Watch))
            if(string.IsNullOrWhiteSpace(item.Text) || item.Text.Length>1200 || item.EvidenceIds.Length==0 || item.EvidenceIds.Length>8 || item.EvidenceIds.Any(id=>!allowed.Contains(id)))
                throw new InvalidDataException("模型引用了本次快照不存在的证据，或未给出可核对的引用。");
    }
}
