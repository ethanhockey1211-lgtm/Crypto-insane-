using TradingScanner.Core.Market;

namespace TradingScanner.Analytics.Structure;

public sealed class StructureOptions
{
    /// <summary>Timeframes on which swings/levels are maintained.</summary>
    public Timeframe[] Timeframes { get; set; } = [Timeframe.M5, Timeframe.M15, Timeframe.H1];
    /// <summary>Bars on each side required for a fractal swing.</summary>
    public int SwingStrength { get; set; } = 3;
    public int MaxSwings { get; set; } = 60;
    /// <summary>Swings closer than this many ATRs join the same level.</summary>
    public double LevelToleranceAtr { get; set; } = 0.35;
    /// <summary>Fallback tolerance when ATR is not ready (fraction of price).</summary>
    public double LevelTolerancePct { get; set; } = 0.002;
    /// <summary>Levels whose last touch is older than this many bars are dropped.</summary>
    public int LevelMaxAgeBars { get; set; } = 400;
    public int MaxLevels { get; set; } = 24;
    /// <summary>Lookback for the range high/low, in bars of the structure timeframe.</summary>
    public int RangeLookback { get; set; } = 48;
}
