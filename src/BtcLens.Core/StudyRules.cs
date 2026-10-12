namespace BtcLens.Core;

public sealed record StudyParameters(int PivotSize=2,int SweepLookback=20,decimal BodyAtr=1.2m,decimal BodyFraction=.6m,
    decimal PricePercent=.05m,decimal FlowPercent=5m,decimal OiPercent=.1m,decimal NearPercent=.2m,string LowerTimeframe="5m");
public sealed record RuleReview(string Id,bool Reviewed,string Notes);
public sealed record RuleCard(string Id,string Title,string Layer,string Summary,string SourceFile,string SourceHash,string[] Anchors,string SourceStatus);
public sealed record RuleProfile(int Revision,long CreatedAt,StudyParameters Parameters,RuleReview[] Reviews)
{
    public static RuleProfile Default=>new(0,0,new(),StudyRules.Cards.Select(c=>new RuleReview(c.Id,false,"")).ToArray());
}
public sealed record StudyIntent(int Revision,long ChosenAt,Bias Bias,decimal? Target,decimal? Invalidation,string TargetReason,bool TargetReviewed)
{
    public static StudyIntent Empty=>new(0,0,Bias.Neutral,null,null,"",false);
}
public static class StudyRules
{
    private const string L34="034-market-structure-vs-liquidity-imbalance.md",H34="004349677d50362fd3389f59ebbc9ca75d52d6b808e6d7f701cbaae40f137784";
    public static readonly RuleCard[] Cards=
    [
        new("L34-roles","分开定义证据角色","context","目标、POI、流动性事件和结构确认分别记录；FVG 不因未回访就自动成为主目标。",L34,H34,["video_1 05:32","笔记 §5 Decision Roles"],"用户上传的二次整理笔记；原图和逐句时间戳未独立复核"),
        new("L34-target","先明确目标","target","缺少明确目标时保持观察；目标来源资格、可用性与确认分开审核。",L34,H34,["video_1 05:32"],"笔记支持；具体目标资格仍待个人复核"),
        new("L34-timeframes","区分高低周期","context","高周期背景与未解决的高周期目标不能被孤立的低周期破位自动覆盖。",L34,H34,["笔记 §4 Existing User-Reviewed Decisions"],"历史选择的记录，需用户复核"),
        new("L34-quality","清扫、位移与新 FVG","confirmation","清扫后伴随位移及新 FVG 的结构破位，可作为较强的参考候选；定量阈值并非课程已证明的个人标准。",L34,H34,["video_3 02:06 / 02:56","video_5 04:30"],"支持质量比较；强弱阈值未定"),
        new("L34-conflict","两侧吸引力保留冲突","conflict","两侧均存在未解决的流动性或失衡时列出冲突；不凭一句权重偏好自动选边。",L34,H34,["video_1 07:27–08:46","笔记 §5 Review Gate"],"优先级与几何关系待复核"),
        new("L34-new-route","失败后建立新路线","invalidation","未触及目标不能恢复已失败的候选；再次观察需新的结构、POI/回访和失效依据。",L34,H34,["笔记 §2 Existing user-reviewed decisions"],"历史选择记录；不自动授权再次入场"),
        new("L001-msb","有意义的结构点","confirmation","课程 MSB 关注制造重要极值的 LH/HL。固定 N 根摆动点仅是参考检测，不等于有意义的结构点。","001-msb.md","32d311d29da14dc83c12bf5dc18336d5b4e827411ca8144028fc52a76362b752",["笔记 §3 / §4"],"二次提取，needs_review"),
        new("L010-sfp","区分回收与继续突破","liquidity","刺穿重要摆动点后未站稳才是 SFP 参考；强实体持续站稳、盘整与下一根反包应分别审视。","010-sfp.md","c2d3c2ee4549068768327f08bfbfccf142dbe6e71b8414d7bf868c0839398b5b",["笔记 §3 / §4"],"具体摆动资格和幅度待复核")
    ];
    public static void Validate(RuleProfile profile)
    {
        var p=profile.Parameters;
        if(p.PivotSize is <1 or >10 || p.SweepLookback is <10 or >100 || p.BodyAtr is <.5m or >5m || p.BodyFraction is <.3m or >.95m ||
            p.PricePercent is <.001m or >5m || p.FlowPercent is <.1m or >50m || p.OiPercent is <.001m or >10m || p.NearPercent is <.01m or >2m || p.LowerTimeframe is not ("5m" or "15m"))
            throw new ArgumentException("参考参数超出允许范围，请检查周期、摆动和成交量阈值。");
        if(profile.Reviews.Length!=Cards.Length || profile.Reviews.Select(r=>r.Id).Distinct().Count()!=Cards.Length ||
            profile.Reviews.Any(r=>!Cards.Any(c=>c.Id==r.Id) || r.Notes.Length>2000))throw new ArgumentException("规则卡不完整或备注超过 2000 字。");
    }
    public static void ValidateIntent(StudyIntent intent,MarketView market)
    {
        if(intent.TargetReason.Length>2000 || intent.Target is <=0 || intent.Invalidation is <=0)throw new ArgumentException("目标与失效价须为正数，说明最多 2000 字。");
        if(intent.Target is null && intent.Invalidation is null)return;
        if(intent.Bias==Bias.Neutral || market.Quote is not {} q || market.Cutoff-q.Timestamp>30_000 || q.Timestamp>market.Cutoff)
            throw new ArgumentException("请先选择观察方向并等待新鲜报价。");
        if(intent.Target is {} target && (intent.Bias==Bias.Bullish?target<=q.Price:target>=q.Price))throw new ArgumentException("目标须位于当前价格沿观察方向的前方。");
        if(intent.Invalidation is {} stop && (intent.Bias==Bias.Bullish?stop>=q.Price:stop<=q.Price))throw new ArgumentException("失效参考须在当前价格的相反方向。");
        if(intent.TargetReviewed && (intent.Target is null || string.IsNullOrWhiteSpace(intent.TargetReason)))throw new ArgumentException("确认目标角色前请填写目标和来源说明。");
    }
}
