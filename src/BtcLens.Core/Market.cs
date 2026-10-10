namespace BtcLens.Core;

public static class Timeframes
{
    public static readonly string[] All = ["5m", "15m", "30m", "1H", "4H", "1D"];
    public static long Milliseconds(string timeframe) => timeframe switch
    {
        "5m" => 300_000, "15m" => 900_000, "30m" => 1_800_000,
        "1H" => 3_600_000, "4H" => 14_400_000, "1D" => 86_400_000,
        "1W" => 604_800_000, _ => throw new ArgumentException("Unsupported timeframe.")
    };
}

public sealed record Candle(string Instrument, string Timeframe, long OpenTime,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, bool Confirmed)
{
    public long CloseTime => OpenTime + Timeframes.Milliseconds(Timeframe);
    public bool Valid => Open > 0 && Close > 0 && Low > 0 && High >= Low &&
        High >= Math.Max(Open, Close) && Low <= Math.Min(Open, Close) && Volume >= 0;
}

public sealed record InstrumentInfo(string Id, decimal TickSize, decimal ContractValue, string ContractCurrency);
public sealed record PriceSample(decimal Price, long Timestamp, string Transport);
public enum ZoneKind { Fvg, OrderBlock }
public enum Bias { Bullish, Bearish, Neutral }
public enum ZoneLife { Active, Touched, Filled, Invalidated, Unknown }
public sealed record DetectedZone(string Id, string Instrument, string Timeframe, ZoneKind Kind, Bias Bias,
    decimal Lower, decimal Upper, long SourceTime, long KnownAt, string Algorithm,
    ZoneLife Life = ZoneLife.Active, long? ChangedAt = null);
public sealed record KeyLevel(string Name, decimal Price, string Source, long KnownAt);

public sealed record MarketView(string Mode, string Scope, string Status, string Transport, string? Error,
    InstrumentInfo? Instrument, PriceSample? Quote, long Cutoff, IReadOnlyDictionary<string, Candle[]> Candles,
    DetectedZone[] Zones, KeyLevel[] Levels, int ReplayIndex, int ReplayTotal)
{
    public FlowView Flow { get; init; } = FlowView.Empty;
    public static MarketView Empty => new("demo", "demo", "正在初始化", "离线回放", null, null, null, 0,
        new Dictionary<string, Candle[]>(), [], [], 0, 0);
}
