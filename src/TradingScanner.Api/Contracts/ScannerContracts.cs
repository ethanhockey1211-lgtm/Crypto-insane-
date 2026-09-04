using TradingScanner.Signals.Scanner;
using TradingScanner.Signals.Tape;

namespace TradingScanner.Api.Contracts;

/// <summary>Compact per-symbol row for the real-time scanner stream and heatmap. Full detail lives at /api/scanner/{symbol}.</summary>
public sealed record ScannerRowDto(
    int Rank,
    string Symbol,
    double Score,
    string Setup,
    string Confidence,
    double Price,
    double? Entry,
    double? Stop,
    double? Target1,
    double? RR,
    double? R1m,
    double? R5m,
    double? R15m,
    double? R1h,
    double? R24h,
    double? RelVol,
    string? Breakout,
    bool DoNotChase,
    bool Stale,
    double? VwapDev,
    double? Volume24h,
    double? KeyLevel,
    string? Trend,
    /// <summary>Component points in fixed order: momentum, volume, structure, breakout, market, liquidity, risk/reward.</summary>
    double[] Components,
    double Penalty,
    /// <summary>Watch, InZone, Late or Chase (see <see cref="EntryState"/>); null without a plan.</summary>
    string? EntryState,
    double? ChaseCeiling)
{
    private static readonly string[] ComponentOrder = [OpportunityScorer.Momentum, OpportunityScorer.Volume, OpportunityScorer.Structure, OpportunityScorer.Breakout, OpportunityScorer.Market, OpportunityScorer.Liquidity, OpportunityScorer.RiskReward];

    public static ScannerRowDto From(Opportunity o)
    {
        var components = new double[ComponentOrder.Length];
        for (var i = 0; i < components.Length; i++) components[i] = o.Breakdown.ComponentPoints(ComponentOrder[i]);
        return new(
            o.Rank, o.Symbol.Value, o.Score, MarketTape.Label(o.Setup.Type), o.Setup.Confidence.ToString(), o.Price,
            o.Plan?.EntryMid, o.Plan?.Stop, o.Plan?.Target1, o.Plan?.RewardRatio1,
            o.Metrics.R1m, o.Metrics.R5m, o.Metrics.R15m, o.Metrics.R1h, o.Metrics.R24h, o.Metrics.RelVol5m,
            o.Setup.Breakout?.State.ToString(), o.Overextension.DoNotChase, o.Quality.Stale, o.Metrics.VwapDeviationPct, o.Metrics.Volume24hQuote,
            o.Setup.KeyLevel, o.Metrics.Alignment5m, components, o.Breakdown.Penalties.Sum(p => p.Points),
            o.Plan?.EntryState.ToString(), o.Plan?.ChaseCeiling);
    }
}

public sealed record ScannerStreamDto(DateTimeOffset At, MarketContext Market, IReadOnlyList<ScannerRowDto> Rows, int Universe, double CycleMs);
