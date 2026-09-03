using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;

namespace TradingScanner.Signals;

public interface ISignalsReader
{
    BreakoutAnalysis? GetBreakouts(Symbol symbol);
}

/// <summary>
/// Chains on <see cref="AnalyticsEngine.SnapshotUpdated"/> (engine thread) so every closed bar of the breakout
/// timeframe advances the symbol's level trackers. History rebuilds replay bars chronologically through the same path.
/// </summary>
public sealed class SignalsEngine : ISignalsReader, IDisposable
{
    private readonly AnalyticsEngine _analytics;
    private readonly SignalsOptions _o;
    private readonly ILogger<SignalsEngine> _logger;
    private readonly ConcurrentDictionary<Symbol, SymbolBreakoutTracker> _breakouts = new();
    private readonly ConcurrentDictionary<Symbol, BreakoutAnalysis> _snapshots = new();

    public SignalsEngine(AnalyticsEngine analytics, IOptions<SignalsOptions> options, ILogger<SignalsEngine> logger)
    {
        _analytics = analytics;
        _o = options.Value;
        _logger = logger;
        _analytics.SnapshotUpdated += OnSnapshot;
        _analytics.RebuildStarting += OnRebuildStarting;
    }

    public BreakoutAnalysis? GetBreakouts(Symbol symbol) => _snapshots.GetValueOrDefault(symbol);

    private void OnRebuildStarting(Symbol symbol)
    {
        if (_breakouts.TryGetValue(symbol, out var t)) t.Reset();
        _snapshots.TryRemove(symbol, out _);
    }

    private void OnSnapshot(AnalyticsSnapshot snapshot, Candle? bar)
    {
        if (bar is not { } c || c.Timeframe != _o.Breakout.Timeframe) return;
        var structure = snapshot.StructureFor(c.Timeframe);
        var indicators = snapshot.For(c.Timeframe);
        if (structure is null || indicators is null) return;
        var tracker = _breakouts.GetOrAdd(snapshot.Symbol, static (s, o) => new SymbolBreakoutTracker(s, o), _o.Breakout);
        _snapshots[snapshot.Symbol] = tracker.Update(c, structure, indicators);
    }

    public void Dispose()
    {
        _analytics.SnapshotUpdated -= OnSnapshot;
        _analytics.RebuildStarting -= OnRebuildStarting;
    }
}
