namespace TradingScanner.Analytics;

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";
    public int RsiPeriod { get; set; } = 14;
    public int AtrPeriod { get; set; } = 14;
    public int RelativeVolumeBaseline { get; set; } = 20;
    public int RelativeVolumeFast { get; set; } = 3;
    public int RealizedVolatilityPeriod { get; set; } = 20;
    /// <summary>Short ATR used for the volatility compression/expansion ratio.</summary>
    public int AtrFastPeriod { get; set; } = 5;
    /// <summary>Bars over which taker buy share is measured.</summary>
    public int BuyShareBars { get; set; } = 10;
    /// <summary>Number of recent 1m log returns kept for cross-asset correlation.</summary>
    public int RecentReturnBars { get; set; } = 60;
    public Structure.StructureOptions Structure { get; set; } = new();
}
