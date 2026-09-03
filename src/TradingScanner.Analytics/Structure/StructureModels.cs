using TradingScanner.Core.Market;

namespace TradingScanner.Analytics.Structure;

public enum SwingType : byte { High = 0, Low = 1 }

/// <summary>A confirmed fractal swing. Confirmed only after <c>strength</c> later bars closed, so it is never known early.</summary>
public readonly record struct SwingPoint(SwingType Type, double Price, DateTimeOffset BarTime, long BarIndex, DateTimeOffset ConfirmedAt);

public enum LevelSource : byte
{
    /// <summary>Cluster of swing highs/lows.</summary>
    Swing = 0,
    /// <summary>Highest high / lowest low over a lookback window.</summary>
    Range = 1,
    /// <summary>UTC session high / low.</summary>
    Session = 2,
}

/// <summary>
/// A horizontal price level. Whether it acts as support or resistance depends on where price is now, so the
/// classification is done at read time (see <see cref="StructureSnapshot.NearestResistance"/>).
/// </summary>
public sealed record PriceLevel(
    string Id,
    double Price,
    LevelSource Source,
    int Touches,
    DateTimeOffset FirstTouch,
    DateTimeOffset LastTouch,
    long LastTouchBarIndex,
    /// <summary>Touches weighted by recency; higher = more significant.</summary>
    double Strength);

public enum StructureTrend : byte { Unknown = 0, Uptrend = 1, Downtrend = 2, Range = 3 }

public sealed record StructureSnapshot(
    Timeframe Timeframe,
    DateTimeOffset AsOf,
    long BarIndex,
    double Close,
    double? Atr,
    IReadOnlyList<SwingPoint> Swings,
    IReadOnlyList<PriceLevel> Levels,
    StructureTrend Trend,
    /// <summary>Most recent swing labels oldest→newest, e.g. "HL HH HL HH".</summary>
    string TrendLabels,
    double? RangeHigh,
    double? RangeLow,
    int RangeLookback,
    double? SessionHigh,
    double? SessionLow)
{
    public PriceLevel? NearestResistance => NearestAbove(Close);
    public PriceLevel? NearestSupport => NearestBelow(Close);

    public PriceLevel? NearestAbove(double price)
    {
        PriceLevel? best = null;
        foreach (var l in Levels) if (l.Price > price && (best is null || l.Price < best.Price)) best = l;
        return best;
    }

    public PriceLevel? NearestBelow(double price)
    {
        PriceLevel? best = null;
        foreach (var l in Levels) if (l.Price < price && (best is null || l.Price > best.Price)) best = l;
        return best;
    }

    /// <summary>Distance from price to the level as a fraction of price (positive = level above).</summary>
    public static double DistancePct(double price, PriceLevel level) => price > 0 ? (level.Price - price) / price : double.NaN;
}
