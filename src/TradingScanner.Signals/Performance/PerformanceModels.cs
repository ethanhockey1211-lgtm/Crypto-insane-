namespace TradingScanner.Signals.Performance;

public sealed record SignalRecord(
    Guid Id,
    string Symbol,
    DateTimeOffset At,
    string Setup,
    string Confidence,
    double Score,
    IReadOnlyDictionary<string, double> Components,
    double Penalty,
    double Price,
    double? Entry,
    double? Stop,
    double? Target1,
    double? Target2,
    double? Target3,
    double? RewardRatio1,
    string Regime,
    string? BtcTrend,
    bool DoNotChase,
    int ConfigVersion);

public sealed record SignalOutcome(
    Guid SignalId,
    double? Ret5m,
    double? Ret15m,
    double? Ret30m,
    double? Ret1h,
    double? Ret2h,
    /// <summary>Best excursion from the signal price, as a fraction.</summary>
    double Mfe,
    /// <summary>Worst excursion from the signal price, as a fraction (≤ 0).</summary>
    double Mae,
    bool? StopHit,
    bool? Target1Hit,
    bool? Target2Hit,
    bool? Target3Hit,
    /// <summary>"stop", "t1", "t2", "t3" or "none": which plan level was touched first.</summary>
    string FirstEvent,
    /// <summary>Outcome in R: +RR1 when T1 was hit before the stop, −1 when the stop was hit first, else 1h return over risk per unit.</summary>
    double? R,
    DateTimeOffset LastPrice,
    bool Complete);

public sealed record SignalWithOutcome(SignalRecord Signal, SignalOutcome Outcome);

public sealed record PerformanceBucket(
    string Key,
    int Signals,
    int Completed,
    int WithPlan,
    /// <summary>Share of completed signals with a plan where target 1 was hit before the stop.</summary>
    double? TargetBeforeStopRate,
    double? StopRate,
    double? AvgR,
    double? Expectancy,
    double? ProfitFactor,
    double? AvgRet5m,
    double? AvgRet15m,
    double? AvgRet30m,
    double? AvgRet1h,
    double? AvgRet2h,
    /// <summary>Share of completed signals whose return at the horizon was positive (null when none have reached it).</summary>
    double? PositiveRate15m,
    double? PositiveRate30m,
    double? PositiveRate1h,
    double? PositiveRate2h,
    double? AvgMfe,
    double? AvgMae);

public sealed record PerformanceReport(
    DateTimeOffset At,
    PerformanceBucket Overall,
    IReadOnlyList<PerformanceBucket> ByScoreBucket,
    IReadOnlyList<PerformanceBucket> BySetup,
    IReadOnlyList<PerformanceBucket> ByRegime,
    IReadOnlyList<PerformanceBucket> BySymbol,
    IReadOnlyList<PerformanceBucket> ByConfidence,
    int ConfigVersion,
    string Note);

public sealed class PerformanceOptions
{
    /// <summary>Score at which an opportunity with a setup is recorded as a signal.</summary>
    public double RecordThreshold { get; set; } = 60;
    /// <summary>The same symbol+setup is not recorded again within this window.</summary>
    public TimeSpan DedupeWindow { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan Horizon { get; set; } = TimeSpan.FromHours(2);
    public int MaxRecords { get; set; } = 20_000;
}
