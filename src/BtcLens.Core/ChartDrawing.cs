namespace BtcLens.Core;

public enum DrawingKind { Rectangle, HorizontalLine, TrendLine }
public sealed record ChartDrawing(string Id, string Scope, int Revision, string Name, DrawingKind Kind,
    long Start, long End, decimal Price1, decimal Price2, long CreatedAt)
{
    public long? DeletedAt { get; init; }
    public decimal Lower => Math.Min(Price1, Price2);
    public decimal Upper => Math.Max(Price1, Price2);
}
public sealed record ChartAnchor(string? DrawingId, long Start, long End, int? DrawingRevision = null);
public sealed record ChartObjectSelection(string Kind, string Id);
public sealed record DrawingChange(string? Id, int? Revision, string Kind, long Start, long End,
    decimal Price1, decimal Price2, string Scope);
public static class DrawingRules
{
    public static void Validate(ChartDrawing drawing)
    {
        if (string.IsNullOrWhiteSpace(drawing.Name) || drawing.Name.Length > 100 || !Enum.IsDefined(drawing.Kind))
            throw new ArgumentException("填写绘图名称和有效类型。");
        if (drawing.Start <= 0 || drawing.End < drawing.Start || drawing.Price1 <= 0 || drawing.Price2 <= 0)
            throw new ArgumentException("绘图时间和价格范围无效。");
        if (drawing.Kind == DrawingKind.Rectangle && (drawing.Start == drawing.End || drawing.Price1 == drawing.Price2))
            throw new ArgumentException("矩形需要两个不同的时间与价格位置。");
        if (drawing.Kind == DrawingKind.TrendLine && drawing.Start == drawing.End)
            throw new ArgumentException("趋势线需要两个不同的时间位置。");
        if (drawing.Kind == DrawingKind.HorizontalLine && drawing.Price1 != drawing.Price2)
            throw new ArgumentException("水平线的两个价格必须相同。");
    }
}
