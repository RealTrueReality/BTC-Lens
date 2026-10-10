using BtcLens.Core;
namespace BtcLens.Web.Components.Shared;
public static class LensFormat
{
    public static string Price(decimal? value) => value?.ToString("N1", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
    public static string Money(decimal? value) => value?.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "待补全";
    public static string Time(long timestamp) => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm");
    public static string Time(DateTimeOffset timestamp) => timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm");
    public static string Kind(ZoneKind kind) => kind == ZoneKind.Fvg ? "FVG" : "OB";
    public static string BiasName(Bias bias) => bias == Bias.Bullish ? "看涨" : bias == Bias.Bearish ? "看跌" : "待观察";
    public static string Life(ZoneLife life) => life switch { ZoneLife.Active => "未回访", ZoneLife.Touched => "已触及", ZoneLife.Filled => "已填补", ZoneLife.Invalidated => "已失效", _ => "未知" };
}
