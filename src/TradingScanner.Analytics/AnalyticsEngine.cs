using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;

namespace TradingScanner.Analytics;

/// <summary>
/// Observes the market-state engine (on its thread), maintains <see cref="SymbolAnalytics"/> per symbol,
/// and publishes immutable snapshots for the API and the scanner. Never computes anything per tick;
/// live-price projections are pure functions on the last snapshot.
/// </summary>
public sealed class AnalyticsEngine : IMarketEventObserver, IAnalyticsReader
{
    private readonly ICandleHistoryReader _candles;
    private readonly AnalyticsOptions _options;
    private readonly ILogger<AnalyticsEngine> _logger;
    private readonly ConcurrentDictionary<Symbol, SymbolAnalytics> _state = new();
    private readonly ConcurrentDictionary<Symbol, AnalyticsSnapshot> _snapshots = new();

    public AnalyticsEngine(ICandleHistoryReader candles, IOptions<AnalyticsOptions> options, ILogger<AnalyticsEngine> logger)
    {
        _candles = candles;
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyCollection<Symbol> Symbols => _snapshots.Keys.ToArray();
    public AnalyticsSnapshot? GetSnapshot(Symbol symbol) => _snapshots.GetValueOrDefault(symbol);
    public AnalyticsProjection? Project(Symbol symbol, double price, DateTimeOffset now) => _snapshots.GetValueOrDefault(symbol)?.Project(price, now);

    public void OnCandleClosed(in Candle candle)
    {
        var s = _state.GetOrAdd(candle.Symbol, static (sym, o) => new SymbolAnalytics(sym, o), _options);
        s.Update(candle);
        _snapshots[candle.Symbol] = s.Snapshot();
    }

    public void OnHistoryApplied(Symbol symbol)
    {
        var s = _state.GetOrAdd(symbol, static (sym, o) => new SymbolAnalytics(sym, o), _options);
        s.Rebuild(_candles);
        _snapshots[symbol] = s.Snapshot();
        _logger.LogDebug("Analytics rebuilt for {Symbol}", symbol);
    }

    public void OnQuote(PriceQuote quote) { }
    public void OnFeedStatus(FeedStatusChange change) { }
    public void OnGap(DataGap gap) { }
}
