using SqlSugar;
namespace BtcLens.Infrastructure;
[SugarTable("chart_drawings")]
public sealed class DrawingRowEntity
{
    [SugarColumn(IsPrimaryKey = true)] public string Id { get; set; } = "";
    public string Scope { get; set; } = "";
    public long Timestamp { get; set; }
    public string Json { get; set; } = "";
}
