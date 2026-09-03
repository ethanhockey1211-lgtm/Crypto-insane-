using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;

namespace TradingScanner.Signals.Breakouts;

public enum BreakoutState : byte
{
    Watching = 0,
    Approaching = 1,
    /// <summary>Price crossed the level (wick or weak close) without confirmation.</summary>
    Attempt = 2,
    /// <summary>Closed beyond the level with volume and a strong close.</summary>
    Confirmed = 3,
    /// <summary>Pulled back into the level zone after confirmation.</summary>
    Retesting = 4,
    /// <summary>Retest held and price reclaimed the level.</summary>
    RetestHeld = 5,
    /// <summary>Closed back through the level shortly after the break.</summary>
    Failed = 6,
    /// <summary>Too far beyond the level for a sane entry. Do not chase.</summary>
    Extended = 7,
}

public enum BreakoutDirection : byte { Up = 0, Down = 1 }

public sealed class BreakoutOptions
{
    public Timeframe Timeframe { get; set; } = Timeframe.M5;
    /// <summary>Within this many ATRs of the level counts as approaching.</summary>
    public double ApproachAtr { get; set; } = 0.5;
    /// <summary>A close must clear the level by this many ATRs to count.</summary>
    public double ConfirmBufferAtr { get; set; } = 0.1;
    /// <summary>A close back through the level by this many ATRs is a failure.</summary>
    public double FailBufferAtr { get; set; } = 0.2;
    /// <summary>Relative volume required on the breakout bar.</summary>
    public double RelVolConfirm { get; set; } = 1.5;
    /// <summary>Close position within the bar's range (0..1) required on the breakout bar.</summary>
    public double MinCloseStrength { get; set; } = 0.6;
    /// <summary>A pullback to within this many ATRs of the level starts a retest.</summary>
    public double RetestZoneAtr { get; set; } = 0.3;
    /// <summary>Beyond this many ATRs past the level the move is extended.</summary>
    public double ExtendedAtr { get; set; } = 2.0;
    /// <summary>A close back through the level within this many bars of the break is a failed breakout; later it is just a lost level.</summary>
    public int FailWindowBars { get; set; } = 12;
    public int FailedCooldownBars { get; set; } = 12;
    public int MaxBarsInAttempt { get; set; } = 3;
    public int MaxRetestBars { get; set; } = 8;
    /// <summary>Levels farther than this from price are ignored when choosing the active breakout.</summary>
    public double MaxLevelDistanceAtr { get; set; } = 6.0;
}

public sealed record RetestMetrics(
    /// <summary>Signed closest approach to the level in ATRs (negative = pierced through).</summary>
    double ClosestApproachAtr,
    /// <summary>How far the retest pierced the level, in ATRs (0 if it held above).</summary>
    double PenetrationAtr,
    int BarsNearLevel,
    double AvgRelVol,
    double? RecoveryRelVol,
    int? RecoveryBars,
    bool Held);

public sealed record BreakoutStatus(
    PriceLevel Level,
    BreakoutDirection Direction,
    BreakoutState State,
    DateTimeOffset StateSince,
    int BarsInState,
    int? BarsSinceBreakout,
    double? BreakoutRelVol,
    double? BreakoutCloseStrength,
    /// <summary>Confirmed by time beyond the level rather than by volume.</summary>
    bool WeakVolume,
    RetestMetrics? Retest,
    /// <summary>Signed distance of the close beyond the level in ATRs (positive = beyond, in the breakout direction).</summary>
    double DistanceAtr,
    string Narrative)
{
    public bool IsActionable => State is BreakoutState.Approaching or BreakoutState.Attempt or BreakoutState.Confirmed or BreakoutState.Retesting or BreakoutState.RetestHeld;
}

public sealed record BreakoutAnalysis(
    Symbol Symbol,
    Timeframe Timeframe,
    DateTimeOffset AsOf,
    IReadOnlyList<BreakoutStatus> Levels,
    BreakoutStatus? BestUp,
    BreakoutStatus? BestDown);
