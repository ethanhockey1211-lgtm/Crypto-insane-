using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Metrics;

namespace TradingScanner.MarketData.Engine;

/// <summary>
/// The single consumer of the ingestion channel. Owns every <see cref="SymbolState"/>; all mutation happens on
/// this loop, which is what makes the candle/indicator code lock-free. Observers are invoked inline and must
/// return quickly. Wall-clock ticks are injected into the same channel so candle closes for quiet symbols are
/// serialized with trades.
/// </summary>
public sealed class MarketStateEngine : BackgroundService, IMarketStateReader
{
    private readonly MarketEventChannel _channel;
    private readonly IEnumerable<IMarketEventObserver> _observerSource;
    private IMarketEventObserver[] _observers = [];
    private readonly MarketDataOptions _options;
    private readonly MarketDataMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<MarketStateEngine> _logger;
    private readonly ConcurrentDictionary<Symbol, SymbolState> _symbols = new();
    private readonly ConcurrentDictionary<int, FeedStatusChange> _connections = new();
    private readonly List<Candle> _closed = new(16);
    private long _engineErrors;
    private readonly ConcurrentQueue<EngineError> _recentErrors = new();
    private const int RecentErrorCapacity = 8;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _lastTradeAt;

    public MarketStateEngine(
        MarketEventChannel channel,
        IEnumerable<IMarketEventObserver> observers,
        IOptions<MarketDataOptions> options,
        MarketDataMetrics metrics,
        ILogger<MarketStateEngine> logger,
        TimeProvider? time = null)
    {
        _channel = channel;
        // Observers are materialized when the loop starts, not here: an observer may legitimately depend on
        // this engine (as IMarketStateReader), which would otherwise be a construction cycle.
        _observerSource = observers;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyCollection<Symbol> Symbols => _symbols.Keys.ToArray();
    public SymbolState? Get(Symbol symbol) => _symbols.GetValueOrDefault(symbol);
    public PriceQuote? GetQuote(Symbol symbol) => _symbols.GetValueOrDefault(symbol)?.Quote;
    public MarketStats? GetStats(Symbol symbol) => _symbols.GetValueOrDefault(symbol)?.Stats;
    public bool IsHistoryLoaded(Symbol symbol) => _symbols.GetValueOrDefault(symbol)?.HistoryLoaded ?? false;
    public CandleSnapshot? GetCandles(Symbol symbol, Timeframe timeframe, int? lastN = null) => _symbols.GetValueOrDefault(symbol)?.Series(timeframe).Snapshot(lastN);
    public IReadOnlyDictionary<int, FeedStatusChange> ConnectionStatus => _connections;
    public DateTimeOffset? LastEventAt => _lastEventAt;
    public DateTimeOffset? LastTradeAt => _lastTradeAt;
    public long EngineErrors => Interlocked.Read(ref _engineErrors);
    public IReadOnlyList<EngineError> RecentErrors => _recentErrors.ToArray();

    public FeedStatus FeedStatus
    {
        get
        {
            if (_connections.IsEmpty) return FeedStatus.Disconnected;
            var statuses = _connections.Values.Select(c => c.Status).ToArray();
            if (statuses.All(s => s == FeedStatus.Connected)) return FeedStatus.Connected;
            if (statuses.Any(s => s is FeedStatus.Connected or FeedStatus.Degraded)) return FeedStatus.Degraded;
            if (statuses.Any(s => s == FeedStatus.Reconnecting)) return FeedStatus.Reconnecting;
            if (statuses.Any(s => s == FeedStatus.Connecting)) return FeedStatus.Connecting;
            return FeedStatus.Disconnected;
        }
    }

    /// <summary>Pre-create state so the API can list the universe before the first trade arrives.</summary>
    public void RegisterSymbols(IEnumerable<Symbol> symbols)
    {
        foreach (var s in symbols) _symbols.GetOrAdd(s, static (sym, o) => new SymbolState(sym, o), _options);
    }

    /// <summary>Engine-thread only: tell observers a symbol's series were rebuilt from history.</summary>
    public void NotifyHistoryApplied(Symbol symbol)
    {
        foreach (var o in _observers) o.OnHistoryApplied(symbol);
    }

    /// <summary>Run <paramref name="work"/> on the engine thread (used by history warm-up to mutate series safely).</summary>
    public Task PostAsync(Action<MarketStateEngine, List<Candle>> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evt = MarketEvent.FromCommand(() =>
        {
            try { work(this, _closed); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        if (!_channel.Writer.TryWrite(evt))
            return _channel.Writer.WriteAsync(evt, ct).AsTask().ContinueWith(_ => tcs.Task, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Unwrap();
        return tcs.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _observers = _observerSource.ToArray();
        _logger.LogInformation("Market state engine started with {Observers} observer(s)", _observers.Length);
        var clock = RunClockAsync(stoppingToken);
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    Handle(evt);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _engineErrors);
                    var symbol = SymbolOf(evt);
                    _logger.LogError(ex, "Engine failed handling {Kind} event for {Symbol}", evt.Kind, symbol);
                    _recentErrors.Enqueue(EngineError.From(_time.GetUtcNow(), evt.Kind, symbol, ex));
                    while (_recentErrors.Count > RecentErrorCapacity) _recentErrors.TryDequeue(out _);
                }
                _metrics.EventProcessed(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
                _metrics.ChannelDepth(_channel.Depth);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await clock.ConfigureAwait(false);
            _logger.LogInformation("Market state engine stopped");
        }
    }

    private static string? SymbolOf(in MarketEvent evt) => evt.Kind switch
    {
        MarketEventKind.Trade or MarketEventKind.QuoteOnly => evt.Trade.Symbol.Value,
        MarketEventKind.Ticker => evt.Ticker.Symbol.Value,
        MarketEventKind.Gap => evt.Gap?.Symbol.Value,
        _ => null,
    };

    private void Handle(in MarketEvent evt)
    {
        // "Last event" means the last thing the PROVIDER sent. Clock ticks and internal commands are not feed
        // liveness, and a command carries no timestamp at all.
        if (evt.Kind is not (MarketEventKind.ClockTick or MarketEventKind.Command)) _lastEventAt = evt.At;
        _closed.Clear();
        switch (evt.Kind)
        {
            case MarketEventKind.Trade:
            {
                var state = GetOrAdd(evt.Trade.Symbol);
                state.OnTrade(evt.Trade, _closed);
                _lastTradeAt = evt.Trade.ExchangeTime;
                var q = state.Quote!;
                foreach (var o in _observers) o.OnQuote(q);
                break;
            }
            case MarketEventKind.QuoteOnly:
            {
                var state = GetOrAdd(evt.Trade.Symbol);
                state.OnQuoteOnly(evt.Trade);
                if (state.Quote is { } q) foreach (var o in _observers) o.OnQuote(q);
                break;
            }
            case MarketEventKind.Ticker:
            {
                var state = GetOrAdd(evt.Ticker.Symbol);
                state.OnTicker(evt.Ticker);
                foreach (var o in _observers) o.OnQuote(state.Quote!);
                break;
            }
            case MarketEventKind.ClockTick:
                foreach (var state in _symbols.Values) state.OnClock(evt.At, _closed);
                break;
            case MarketEventKind.Status:
                _connections[evt.Status!.ConnectionIndex] = evt.Status;
                foreach (var o in _observers) o.OnFeedStatus(evt.Status);
                break;
            case MarketEventKind.Gap:
                foreach (var o in _observers) o.OnGap(evt.Gap!);
                break;
            case MarketEventKind.Command:
                evt.Command!.Invoke();
                break;
        }

        if (_closed.Count > 0)
        {
            foreach (var c in _closed)
            {
                _metrics.CandleClose();
                foreach (var o in _observers) o.OnCandleClosed(c);
            }
            _closed.Clear();
        }
    }

    private SymbolState GetOrAdd(Symbol symbol) => _symbols.GetOrAdd(symbol, static (sym, o) => new SymbolState(sym, o), _options);

    private async Task RunClockAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // If the channel is full the trade stream itself will close candles; skipping a tick is safe.
                _channel.Writer.TryWrite(MarketEvent.Clock(_time.GetUtcNow()));
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>One exception caught on the engine loop, kept so operators can read it from the status endpoint without log access.</summary>
public sealed record EngineError(DateTimeOffset At, string Kind, string? Symbol, string Error, string? Site)
{
    public static EngineError From(DateTimeOffset at, MarketEventKind kind, string? symbol, Exception ex)
    {
        // First frame inside this codebase, if any: enough to locate the fault without shipping a full stack trace.
        string? site = null;
        foreach (var line in (ex.StackTrace ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("at TradingScanner.", StringComparison.Ordinal)) { site = trimmed; break; }
        }
        return new EngineError(at, kind.ToString(), symbol, $"{ex.GetType().Name}: {ex.Message}", site);
    }
}
