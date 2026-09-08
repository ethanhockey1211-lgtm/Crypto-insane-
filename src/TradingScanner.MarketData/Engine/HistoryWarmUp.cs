using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;

namespace TradingScanner.MarketData.Engine;

/// <summary>
/// Loads REST candle history for every symbol and applies it on the engine thread. Runs concurrently with
/// live streaming; the merge rules in <see cref="SymbolState.ApplyHistory"/> reconcile overlap.
/// </summary>
public sealed class HistoryWarmUp
{
    private static readonly Timeframe[] Preferred = [Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.H1];

    private readonly IMarketDataProvider _provider;
    private readonly MarketStateEngine _engine;
    private readonly MarketDataOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HistoryWarmUp> _logger;
    private readonly object _queueGate = new();
    private readonly LinkedList<Symbol> _pending = new();
    private readonly Dictionary<Symbol, LinkedListNode<Symbol>> _pendingNodes = new();
    private bool _running;

    public int CandlesPerTimeframe { get; init; } = 300;

    public HistoryWarmUp(IMarketDataProvider provider, MarketStateEngine engine, IOptions<MarketDataOptions> options, ILogger<HistoryWarmUp> logger, TimeProvider? time = null)
    {
        _provider = provider;
        _engine = engine;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Move a pending symbol to the front when a user opens it. Repeated calls never create duplicate work.
    /// Returns false for symbols that are unknown, already being loaded, finished, or outside an active warm-up.
    /// </summary>
    public bool Prioritize(Symbol symbol)
    {
        lock (_queueGate)
        {
            if (!_running || !_pendingNodes.TryGetValue(symbol, out var node)) return false;
            _pending.Remove(node);
            _pending.AddFirst(node);
            return true;
        }
    }

    /// <param name="progress">Invoked after every symbol (from worker threads) with the running totals.</param>
    public async Task<WarmUpResult> WarmUpAsync(IReadOnlyCollection<Symbol> symbols, CancellationToken ct, Action<WarmUpResult>? progress = null)
    {
        var timeframes = Preferred.Where(_provider.HistoricalTimeframes.Contains).ToArray();
        var priorities = _options.AlwaysInclude
            .Select((symbol, index) => (symbol, index))
            .GroupBy(x => x.symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        // Stable ordering retains the universe's volume rank after the BTC/ETH regime dependencies.
        var ordered = symbols.Distinct().OrderBy(s => priorities.GetValueOrDefault(s.Value, int.MaxValue)).ToArray();
        lock (_queueGate)
        {
            if (_running) throw new InvalidOperationException("History warm-up is already running.");
            _running = true;
            foreach (var symbol in ordered) _pendingNodes[symbol] = _pending.AddLast(symbol);
        }
        var done = 0;
        var failed = 0;
        string? lastError = null;
        void Publish(bool complete) => progress?.Invoke(new WarmUpResult(ordered.Length, Volatile.Read(ref done), Volatile.Read(ref failed), Volatile.Read(ref lastError), complete));
        bool TryTake(out Symbol symbol)
        {
            lock (_queueGate)
            {
                if (_pending.First is not { } node) { symbol = default; return false; }
                symbol = node.Value;
                _pending.RemoveFirst();
                _pendingNodes.Remove(symbol);
                return true;
            }
        }
        async Task WorkerAsync()
        {
            while (!ct.IsCancellationRequested && TryTake(out var symbol))
            {
                try
                {
                    var loaded = new Dictionary<Timeframe, IReadOnlyList<Candle>>();
                    var retry = new List<Timeframe>();
                    async Task LoadAsync(Timeframe tf)
                    {
                        // Align both ends so a mid-bucket startup still requests 300 complete closed buckets.
                        var to = tf.BucketStart(_time.GetUtcNow());
                        var from = to - tf.Duration() * CandlesPerTimeframe;
                        loaded[tf] = await _provider.GetHistoricalCandlesAsync(symbol, tf, from, to, ct).ConfigureAwait(false);
                    }
                    foreach (var tf in timeframes)
                    {
                        try { await LoadAsync(tf).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            retry.Add(tf);
                            _logger.LogWarning(ex, "History warm-up will retry {Symbol} {Timeframe} after its other timeframes", symbol, tf);
                        }
                    }
                    // Keep successes in this worker only. A single failed request no longer discards them or
                    // skips the remaining granularities; retries still use the provider's shared rate limiter.
                    foreach (var tf in retry) await LoadAsync(tf).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    await _engine.PostAsync((engine, closed) =>
                    {
                        var state = engine.Get(symbol) ?? throw new InvalidOperationException($"{symbol} is not registered in the market engine.");
                        foreach (var tf in timeframes) state.ApplyHistory(tf, loaded[tf]);
                        state.RebuildDerived(closed);
                        engine.NotifyHistoryApplied(symbol);
                    }, ct).WaitAsync(ct).ConfigureAwait(false);
                    var n = Interlocked.Increment(ref done);
                    if (n % 25 == 0 || n == ordered.Length) _logger.LogInformation("History warm-up {Done}/{Total}", n, ordered.Length);
                    Publish(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Volatile.Write(ref lastError, $"{symbol.Value}: {ex.GetType().Name}: {ex.Message}");
                    _logger.LogWarning(ex, "History warm-up failed for {Symbol}", symbol);
                    Publish(false);
                }
            }
        }
        try
        {
            Publish(false);
            // Only four tasks exist, regardless of catalog size. Pending work can be reprioritized without
            // starting new workers or exceeding the provider's configured REST request budget.
            await Task.WhenAll(Enumerable.Range(0, Math.Min(4, ordered.Length)).Select(_ => WorkerAsync())).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("History warm-up complete: {Ok} ok, {Failed} failed", done, failed);
            var result = new WarmUpResult(ordered.Length, done, failed, lastError, true);
            progress?.Invoke(result);
            return result;
        }
        finally
        {
            lock (_queueGate)
            {
                _running = false;
                _pending.Clear();
                _pendingNodes.Clear();
            }
        }
    }
}

/// <summary>Progress/outcome of REST history warm-up, published to the status endpoint.</summary>
public sealed record WarmUpResult(int Total, int Loaded, int Failed, string? LastError, bool Complete)
{
    public static readonly WarmUpResult Empty = new(0, 0, 0, null, false);
}
