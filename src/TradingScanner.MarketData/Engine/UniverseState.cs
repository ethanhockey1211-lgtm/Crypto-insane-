using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;

namespace TradingScanner.MarketData.Engine;

/// <summary>The currently selected tradable universe, published by the orchestrator.</summary>
public sealed class UniverseState
{
    private IReadOnlyList<ProductInfo> _products = [];
    public IReadOnlyList<ProductInfo> Products => Volatile.Read(ref _products);
    public IReadOnlyList<Symbol> Symbols => Products.Select(p => p.Symbol).ToArray();
    public DateTimeOffset? SelectedAt { get; private set; }
    /// <summary>Startup progress: Starting, SelectingUniverse, Streaming, Failed.</summary>
    public string Phase { get; private set; } = "Starting";
    public string? LastError { get; private set; }
    public int Attempts { get; private set; }
    /// <summary>Candidate products whose 24h stats could not be fetched and were therefore excluded from ranking.</summary>
    public int StatsUnavailable { get; private set; }
    public WarmUpResult WarmUp { get; private set; } = WarmUpResult.Empty;

    public void Set(IReadOnlyList<ProductInfo> products, DateTimeOffset at, int statsUnavailable = 0)
    {
        Volatile.Write(ref _products, products);
        SelectedAt = at;
        StatsUnavailable = statsUnavailable;
        Phase = "Streaming";
        LastError = null;
    }

    public void ReportWarmUp(WarmUpResult result) => WarmUp = result;

    public void Report(string phase, string? error = null)
    {
        Phase = phase;
        if (error is not null) { LastError = error; Attempts++; }
    }
}
