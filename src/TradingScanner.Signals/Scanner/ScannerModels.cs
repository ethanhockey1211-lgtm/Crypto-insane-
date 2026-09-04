using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;

namespace TradingScanner.Signals.Scanner;

public enum SetupType : byte
{
    None = 0,
    Breakout = 1,
    BreakoutRetest = 2,
    VwapReclaim = 3,
    SupportBounce = 4,
    MomentumContinuation = 5,
    RangeBreakout = 6,
    TrendPullback = 7,
    Reversal = 8,
    VolumeExpansion = 9,
    VolatilityExpansion = 10,
}

public enum Confidence : byte { Low = 0, Medium = 1, High = 2 }
public enum TrendBias : byte { Neutral = 0, Bullish = 1, Bearish = 2 }
public enum MarketRegime : byte { StrongRiskOff = 0, RiskOff = 1, Neutral = 2, RiskOn = 3, StrongRiskOn = 4 }

public sealed record AssetState(
    string Symbol,
    double Price,
    double? R5m,
    double? R15m,
    double? R1h,
    double? R24h,
    TrendBias Trend,
    bool? AboveVwap,
    double? VwapSigma,
    double? AtrPct,
    double? RelVol5m,
    bool Dumping,
    string Summary);

public sealed record MarketContext(
    DateTimeOffset At,
    MarketRegime Regime,
    bool AltsFavorable,
    AssetState? Btc,
    AssetState? Eth,
    double BreadthAboveVwap,
    double BreadthPositive1h,
    double BreadthBullishAlignment,
    double MedianRelVolume,
    int SymbolsEvaluated,
    IReadOnlyList<string> Notes);

public sealed record ScoreComponent(string Name, double Points, double Max, string Evidence);

public sealed record ScoreBreakdown(IReadOnlyList<ScoreComponent> Components, IReadOnlyList<ScoreComponent> Penalties, double Raw, double Total, int ConfigVersion)
{
    public double ComponentPoints(string name) { foreach (var c in Components) if (c.Name == name) return c.Points; return 0; }
    public double PenaltyPoints(string name) { foreach (var c in Penalties) if (c.Name == name) return c.Points; return 0; }
}

public sealed record TradePlan(
    double EntryLow,
    double EntryHigh,
    string Trigger,
    double Invalidation,
    double Stop,
    double Target1,
    double Target2,
    double Target3,
    double RiskPerUnit,
    double RewardRatio1,
    double RewardRatio2,
    double RewardRatio3,
    IReadOnlyList<string> Basis,
    /// <summary>Price above which fewer than <see cref="ScoringConfig.ChaseMinRewardRatio"/> R remain to target 1. Do not enter above it.</summary>
    double ChaseCeiling,
    /// <summary>Where the current price sits relative to the entry zone and the chase ceiling.</summary>
    EntryState EntryState)
{
    public double EntryMid => (EntryLow + EntryHigh) / 2;

    /// <summary>The ceiling wins over the zone: when resistance is close it can sit inside the entry zone, and the part of the zone above it is not enterable.</summary>
    public static EntryState StateFor(double price, double entryLow, double entryHigh, double chaseCeiling) =>
        price > chaseCeiling ? EntryState.Chase
        : price < entryLow ? EntryState.Watch
        : price <= entryHigh ? EntryState.InZone
        : EntryState.Late;
}

/// <summary>
/// Watch: price is below the entry zone, wait for the trigger. InZone: inside the planned entry. Late: above the zone
/// but the reward to target 1 is still acceptable. Chase: above the ceiling, the plan's reward is gone.
/// </summary>
public enum EntryState : byte { Watch = 0, InZone = 1, Late = 2, Chase = 3 }

public sealed record OverextensionAssessment(
    double Score,
    bool DoNotChase,
    IReadOnlyList<string> Flags,
    double? VwapSigma,
    double? Ema20DistanceAtr,
    double? Move5mAtr,
    double? Move15mAtr,
    double? Move1hPct,
    double? Move24hPct,
    double? SupportDistanceAtr);

public sealed record SetupClassification(SetupType Type, Confidence Confidence, TrendBias Bias, IReadOnlyList<string> Evidence, BreakoutStatus? Breakout, double? KeyLevel);

public sealed record OpportunityMetrics(
    double? R1m, double? R5m, double? R15m, double? R1h, double? R24h,
    double? Accel5m,
    double? RelVol5m, double? BuyShare5m, double? Rsi5m, double? Rsi15m,
    double? Atr5m, double? AtrPct5m,
    double? Vwap, double? VwapDeviationPct, double? VwapSigma,
    double? NearestResistance, double? NearestSupport,
    double? SpreadBps, double? Volume24hQuote,
    double? BtcCorrelation,
    string? Trend5m, string? Trend15m, string? Alignment5m, string? Alignment15m);

public sealed record ScoreChange(double Previous, double Current, IReadOnlyList<string> Reasons);

public sealed record DataQuality(bool Stale, long AgeMs, bool HistoryLoaded, string Provider, string Exchange);

public sealed record Opportunity(
    Symbol Symbol,
    DateTimeOffset At,
    double Price,
    int Rank,
    double Score,
    ScoreBreakdown Breakdown,
    SetupClassification Setup,
    TradePlan? Plan,
    OverextensionAssessment Overextension,
    IReadOnlyList<string> Why,
    string Invalidation,
    IReadOnlyList<string> Risks,
    OpportunityMetrics Metrics,
    ScoreChange? Change,
    DataQuality Quality);

public sealed record ScannerSnapshot(DateTimeOffset At, MarketContext Market, IReadOnlyList<Opportunity> Opportunities, int Universe, double CycleMs)
{
    public Opportunity? Get(Symbol symbol)
    {
        foreach (var o in Opportunities) if (o.Symbol == symbol) return o;
        return null;
    }
}
