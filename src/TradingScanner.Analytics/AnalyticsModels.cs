using TradingScanner.Core.Market;

namespace TradingScanner.Analytics;

public enum EmaAlignment { Unknown = 0, Bullish = 1, Bearish = 2, Mixed = 3 }
public enum CrossDirection { None = 0, Bullish = 1, Bearish = 2 }

/// <summary>Indicator state for one timeframe as of the last CLOSED bar. Null = not enough history yet.</summary>
public sealed record IndicatorValues(
    Timeframe Timeframe,
    DateTimeOffset AsOf,
    double Close,
    double? Ema9,
    double? Ema20,
    double? Ema50,
    double? Ema200,
    double? Rsi,
    double? RsiPrevious,
    double? Atr,
    double? AtrPct,
    double? TrueRange,
    double? RangeRatio,
    double? RelVolume,
    double? VolumeZ,
    double? VolumeFastRatio,
    double? RealizedVolPct,
    EmaAlignment Alignment,
    double? EmaSpreadPct,
    int? BarsSinceEma9x20Cross,
    CrossDirection LastEma9x20Cross,
    double? DistanceToEma20Atr)
{
    /// <summary>Distance of <paramref name="price"/> from EMA20 in ATR units, using closed-bar values.</summary>
    public double? Ema20DistanceAtr(double price) => Ema20 is { } e && Atr is { } a && a > 0 ? (price - e) / a : null;
    public double? Ema20DistancePct(double price) => Ema20 is { } e && e > 0 ? (price - e) / e : null;
}

public sealed record VwapValues(DateTimeOffset SessionStart, double Vwap, double Std, int Bars)
{
    public double DeviationPct(double price) => Vwap > 0 ? (price - Vwap) / Vwap : double.NaN;
    public double? Sigma(double price) => Std > 0 ? (price - Vwap) / Std : null;
}

/// <summary>Reference closes needed to project returns at any live price without re-reading the series.</summary>
public sealed record MomentumValues(double LastClose, IReadOnlyDictionary<int, double> ReferenceCloses, IReadOnlyDictionary<int, double> PreviousWindowCloses)
{
    public double? Return(int minutes, double price) =>
        ReferenceCloses.TryGetValue(minutes, out var r) && r > 0 ? price / r - 1 : null;

    /// <summary>Latest-window return minus the preceding equal-length window's return. Positive = strengthening.</summary>
    public double? Acceleration(int minutes, double price)
    {
        var recent = Return(minutes, price);
        if (recent is null) return null;
        if (!ReferenceCloses.TryGetValue(minutes, out var a) || !PreviousWindowCloses.TryGetValue(minutes, out var b) || a <= 0 || b <= 0) return null;
        return recent - (a / b - 1);
    }

    public MomentumProjection Project(double price) => new(
        Return(1, price), Return(3, price), Return(5, price), Return(15, price), Return(30, price), Return(60, price), Return(240, price), Return(1440, price),
        Acceleration(5, price), Acceleration(15, price), Acceleration(60, price));
}

public sealed record MomentumProjection(
    double? R1m, double? R3m, double? R5m, double? R15m, double? R30m, double? R1h, double? R4h, double? R24h,
    double? Accel5m, double? Accel15m, double? Accel1h);

public sealed record VwapProjection(DateTimeOffset SessionStart, double Vwap, double Std, int Bars, double DeviationPct, double? Sigma, bool Above);

/// <summary>Immutable per-symbol analytics as of the last closed bars. Safe to read from any thread.</summary>
public sealed record AnalyticsSnapshot(
    Symbol Symbol,
    DateTimeOffset AsOf,
    IReadOnlyList<IndicatorValues> Timeframes,
    VwapValues? Vwap,
    MomentumValues? Momentum,
    IReadOnlyList<Structure.StructureSnapshot> Structure)
{
    public IndicatorValues? For(Timeframe tf)
    {
        foreach (var t in Timeframes) if (t.Timeframe == tf) return t;
        return null;
    }

    public Structure.StructureSnapshot? StructureFor(Timeframe tf)
    {
        foreach (var s in Structure) if (s.Timeframe == tf) return s;
        return null;
    }

    /// <summary>Pure projection of live-price dependent values (momentum, VWAP deviation) at <paramref name="price"/>.</summary>
    public AnalyticsProjection Project(double price, DateTimeOffset now) => new(
        Symbol, AsOf, now, price, Timeframes,
        Vwap is { } v ? new VwapProjection(v.SessionStart, v.Vwap, v.Std, v.Bars, v.DeviationPct(price), v.Sigma(price), price > v.Vwap) : null,
        Momentum?.Project(price),
        Structure);
}

public sealed record AnalyticsProjection(
    Symbol Symbol,
    DateTimeOffset AsOf,
    DateTimeOffset ProjectedAt,
    double Price,
    IReadOnlyList<IndicatorValues> Timeframes,
    VwapProjection? Vwap,
    MomentumProjection? Momentum,
    IReadOnlyList<Structure.StructureSnapshot> Structure)
{
    public IndicatorValues? For(Timeframe tf)
    {
        foreach (var t in Timeframes) if (t.Timeframe == tf) return t;
        return null;
    }
}

public interface IAnalyticsReader
{
    IReadOnlyCollection<Symbol> Symbols { get; }
    AnalyticsSnapshot? GetSnapshot(Symbol symbol);
    AnalyticsProjection? Project(Symbol symbol, double price, DateTimeOffset now);
}
