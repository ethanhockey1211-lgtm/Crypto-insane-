using TradingScanner.Core.Market;
using TradingScanner.Signals.Performance;

namespace TradingScanner.Backtest;

/// <summary>Execution assumptions applied to replayed signals. All in basis points of price unless stated.</summary>
public sealed record CostModel(int FeeBps = 10, int SlippageBps = 5, int SpreadBps = 4, int LatencyBars = 1)
{
    /// <summary>Round-trip fee, entry slippage and half the spread, per unit of price.</summary>
    public double CostPerUnit(double price) => price * (2 * FeeBps + SlippageBps + SpreadBps / 2.0) / 10_000.0;
}

public sealed record BacktestRequest(
    IReadOnlyList<Symbol> Symbols,
    DateTimeOffset From,
    DateTimeOffset To,
    CostModel Costs,
    double RecordThreshold = 60,
    TimeSpan? Horizon = null,
    TimeSpan? DedupeWindow = null);

public sealed record BacktestResult(
    BacktestRequest Request,
    IReadOnlyList<SignalWithOutcome> Signals,
    PerformanceReport Gross,
    PerformanceReport Net,
    int BarsProcessed,
    int Evaluations,
    TimeSpan Duration,
    IReadOnlyList<string> Notes);

/// <summary>Source of 1m history for replay (provider REST, database, or a fixture).</summary>
public interface IHistoricalCandleSource
{
    Task<IReadOnlyList<Candle>> GetM1Async(Symbol symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
