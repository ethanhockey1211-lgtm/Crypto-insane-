namespace TradingScanner.Signals.Scanner;

/// <summary>
/// Weights and thresholds for the opportunity score. Versioned so stored scores are attributable to a config.
/// These defaults are a starting point for measurement, not a tuned model: Phase 8/9 exist to test them.
/// </summary>
public sealed class ScoringConfig
{
    public int Version { get; set; } = 1;

    public double MomentumMax { get; set; } = 20;
    public double VolumeMax { get; set; } = 20;
    public double StructureMax { get; set; } = 20;
    public double BreakoutMax { get; set; } = 15;
    public double MarketMax { get; set; } = 10;
    public double LiquidityMax { get; set; } = 10;
    public double RiskRewardMax { get; set; } = 15;

    public double OverextensionPenaltyMax { get; set; } = 20;
    public double WeakVolumePenaltyMax { get; set; } = 15;
    public double NearbyResistancePenaltyMax { get; set; } = 10;
    public double BtcDisagreementPenaltyMax { get; set; } = 10;
    public double SpreadPenaltyMax { get; set; } = 20;
    public double FailedBreakoutPenaltyMax { get; set; } = 20;

    /// <summary>Relative volume that earns full volume credit / no credit.</summary>
    public double RelVolFull { get; set; } = 2.5;
    public double RelVolNone { get; set; } = 1.0;
    /// <summary>Spread (bps) below which liquidity is full credit, above which it is zero and penalized.</summary>
    public double SpreadBpsFull { get; set; } = 5;
    public double SpreadBpsNone { get; set; } = 30;
    /// <summary>24h quote volume for zero / full liquidity credit (log-interpolated).</summary>
    public double LiquidityVolumeLow { get; set; } = 1_000_000;
    public double LiquidityVolumeHigh { get; set; } = 100_000_000;
    /// <summary>Reward:risk to target 1 that earns full / zero credit.</summary>
    public double RrFull { get; set; } = 2.5;
    public double RrNone { get; set; } = 0.8;
    /// <summary>Resistance closer than this many ATRs above price is penalized.</summary>
    public double NearbyResistanceAtr { get; set; } = 1.0;
    /// <summary>Reward:risk to target 1 below which a High-confidence classification is capped at Medium.</summary>
    public double MinRewardRatioForHighConfidence { get; set; } = 2.0;
    /// <summary>The chase ceiling is the price above which less than this reward:risk to target 1 remains.</summary>
    public double ChaseMinRewardRatio { get; set; } = 1.5;
}

public sealed class OverextensionConfig
{
    public double VwapSigmaWarn { get; set; } = 2.0;
    public double VwapSigmaHard { get; set; } = 3.0;
    public double Ema20AtrWarn { get; set; } = 2.0;
    public double Move5mAtrWarn { get; set; } = 1.5;
    public double Move5mAtrHard { get; set; } = 3.0;
    public double Move15mAtrWarn { get; set; } = 3.0;
    public double Move1hPctWarn { get; set; } = 0.05;
    public double Move24hPctWarn { get; set; } = 0.15;
    public double VolumeClimaxZ { get; set; } = 4.0;
    public double VolumeClimaxRsi { get; set; } = 78;
    public double SupportDistanceAtrWarn { get; set; } = 3.0;
    public double DoNotChaseScore { get; set; } = 0.6;
}

public sealed class ScannerOptions
{
    public int IntervalMs { get; set; } = 1000;
    public int TopN { get; set; } = 50;
    public TimeSpan StaleQuoteThreshold { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Opportunities below this score are still ranked but not treated as setups in the tape.</summary>
    public double SetupScoreThreshold { get; set; } = 60;
    public ScoringConfig Scoring { get; set; } = new();
    public OverextensionConfig Overextension { get; set; } = new();
}
