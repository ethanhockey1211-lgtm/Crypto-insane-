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
    double? Volume24h)
{
    public static ScannerRowDto From(Opportunity o) => new(
        o.Rank, o.Symbol.Value, o.Score, MarketTape.Label(o.Setup.Type), o.Setup.Confidence.ToString(), o.Price,
        o.Plan?.EntryMid, o.Plan?.Stop, o.Plan?.Target1, o.Plan?.RewardRatio1,
        o.Metrics.R1m, o.Metrics.R5m, o.Metrics.R15m, o.Metrics.R1h, o.Metrics.R24h, o.Metrics.RelVol5m,
        o.Setup.Breakout?.State.ToString(), o.Overextension.DoNotChase, o.Quality.Stale, o.Metrics.VwapDeviationPct, o.Metrics.Volume24hQuote);
}

public sealed record ScannerStreamDto(DateTimeOffset At, MarketContext Market, IReadOnlyList<ScannerRowDto> Rows, int Universe, double CycleMs);
