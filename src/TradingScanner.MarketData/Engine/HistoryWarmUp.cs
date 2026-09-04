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

    public int CandlesPerTimeframe { get; init; } = 300;

    public HistoryWarmUp(IMarketDataProvider provider, MarketStateEngine engine, IOptions<MarketDataOptions> options, ILogger<HistoryWarmUp> logger, TimeProvider? time = null)
    {
        _provider = provider;
        _engine = engine;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="progress">Invoked after every symbol (from worker threads) with the running totals.</param>
    public async Task<WarmUpResult> WarmUpAsync(IReadOnlyCollection<Symbol> symbols, CancellationToken ct, Action<WarmUpResult>? progress = null)
    {
        var timeframes = Preferred.Where(_provider.HistoricalTimeframes.Contains).ToArray();
        var gate = new SemaphoreSlim(4);
        var done = 0;
        var failed = 0;
        string? lastError = null;
        void Publish(bool complete) => progress?.Invoke(new WarmUpResult(symbols.Count, Volatile.Read(ref done), Volatile.Read(ref failed), Volatile.Read(ref lastError), complete));
        var tasks = symbols.Select(async symbol =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var loaded = new List<(Timeframe tf, IReadOnlyList<Candle> candles)>();
                foreach (var tf in timeframes)
                {
                    var to = _time.GetUtcNow();
                    var from = to - tf.Duration() * CandlesPerTimeframe;
                    var candles = await _provider.GetHistoricalCandlesAsync(symbol, tf, from, to, ct).ConfigureAwait(false);
                    loaded.Add((tf, candles));
                }
                await _engine.PostAsync((engine, closed) =>
                {
                    var state = engine.Get(symbol);
                    if (state is null) return;
                    foreach (var (tf, candles) in loaded) state.ApplyHistory(tf, candles);
                    state.RebuildDerived(closed);
                    engine.NotifyHistoryApplied(symbol);
                }, ct).ConfigureAwait(false);
                var n = Interlocked.Increment(ref done);
                if (n % 25 == 0 || n == symbols.Count) _logger.LogInformation("History warm-up {Done}/{Total}", n, symbols.Count);
                Publish(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Volatile.Write(ref lastError, $"{symbol.Value}: {ex.GetType().Name}: {ex.Message}");
                _logger.LogWarning(ex, "History warm-up failed for {Symbol}", symbol);
                Publish(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _logger.LogInformation("History warm-up complete: {Ok} ok, {Failed} failed", done, failed);
        var result = new WarmUpResult(symbols.Count, done, failed, lastError, true);
        progress?.Invoke(result);
        return result;
    }
}

/// <summary>Progress/outcome of REST history warm-up, published to the status endpoint.</summary>
public sealed record WarmUpResult(int Total, int Loaded, int Failed, string? LastError, bool Complete)
{
    public static readonly WarmUpResult Empty = new(0, 0, 0, null, false);
}
