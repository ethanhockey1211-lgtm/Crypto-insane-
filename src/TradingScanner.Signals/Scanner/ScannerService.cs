using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Tape;

namespace TradingScanner.Signals.Scanner;

public interface IScannerReader
{
    ScannerSnapshot? Latest { get; }
    Opportunity? Get(Symbol symbol);
    MarketTape Tape { get; }
}

/// <summary>
/// Ranks the whole universe on a timer from immutable analytics/signals snapshots and live quotes. Runs on its
/// own thread: nothing here touches engine state. The same <see cref="Evaluate"/> is what a backtest calls.
/// </summary>
public sealed class ScannerService : BackgroundService, IScannerReader
{
    private readonly IAnalyticsReader _analytics;
    private readonly ISignalsReader _signals;
    private readonly ISymbolInfoReader _info;
    private readonly ScannerOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger<ScannerService> _logger;
    private ScannerSnapshot? _latest;

    public ScannerService(IAnalyticsReader analytics, ISignalsReader signals, ISymbolInfoReader info, IOptions<ScannerOptions> options, ILogger<ScannerService> logger, TimeProvider? time = null)
    {
        _analytics = analytics;
        _signals = signals;
        _info = info;
        _o = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Tape = new MarketTape();
    }

    public ScannerSnapshot? Latest => Volatile.Read(ref _latest);
    public Opportunity? Get(Symbol symbol) => Latest?.Get(symbol);
    public MarketTape Tape { get; }
    public event Action<ScannerSnapshot>? SnapshotPublished;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, _o.IntervalMs)), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { RunCycle(); }
                catch (Exception ex) { _logger.LogError(ex, "Scanner cycle failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One full ranking pass over every symbol with analytics and a quote.</summary>
    public ScannerSnapshot RunCycle()
    {
        var sw = Stopwatch.StartNew();
        var now = _time.GetUtcNow();
        var inputs = new List<ScanInput>();
        foreach (var symbol in _info.Symbols)
        {
            var snap = _analytics.GetSnapshot(symbol);
            var quote = _info.GetQuote(symbol);
            if (snap is null || quote is null) continue;
            var price = (double)quote.Price;
            if (price <= 0) continue;
            var stats = _info.GetStats(symbol);
            double? vol24 = stats is { } st && st.Volume24hBase > 0 ? (double)st.Volume24hBase * price : null;
            inputs.Add(new ScanInput(symbol, snap, snap.Project(price, now), quote, vol24, _signals.GetBreakouts(symbol), _info.IsHistoryLoaded(symbol)));
        }

        // Keep every symbol in the rankings for diagnosis, but never let a stale or
        // uninitialized benchmark authorize entries in the rest of the universe.
        var contextInputs = inputs.Where(i => i.HistoryLoaded
            && now - i.Quote.ExchangeTime <= _o.StaleQuoteThreshold
            && now - i.Quote.ExchangeTime >= TimeSpan.FromSeconds(-2)).ToList();
        var market = MarketContextBuilder.Build(contextInputs.Select(i => i.Projection).ToList(), now);
        var btcReturns = contextInputs.FirstOrDefault(i => i.Symbol == MarketContextBuilder.Btc)?.Snapshot.Momentum?.RecentReturns;
        var previous = Latest;
        var list = new List<Opportunity>(inputs.Count);
        foreach (var input in inputs)
        {
            try { list.Add(Evaluate(input, market, btcReturns, previous?.Get(input.Symbol), now)); }
            catch (Exception ex) { _logger.LogError(ex, "Scanner failed for {Symbol}", input.Symbol); }
        }
        list.Sort(static (a, b) => b.Score.CompareTo(a.Score) != 0 ? b.Score.CompareTo(a.Score) : string.CompareOrdinal(a.Symbol.Value, b.Symbol.Value));
        for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Rank = i + 1 };

        var snapshot = new ScannerSnapshot(now, market, list, _info.Symbols.Count, sw.Elapsed.TotalMilliseconds);
        Volatile.Write(ref _latest, snapshot);
        Tape.Observe(previous, snapshot, _o.SetupScoreThreshold);
        SnapshotPublished?.Invoke(snapshot);
        return snapshot;
    }

    public Opportunity Evaluate(ScanInput input, MarketContext market, double[]? btcReturns, Opportunity? previous, DateTimeOffset now) =>
        OpportunityEvaluator.Evaluate(input, market, btcReturns, previous, now, _o);
}

public sealed record ScanInput(Symbol Symbol, AnalyticsSnapshot Snapshot, AnalyticsProjection Projection, PriceQuote Quote, double? Volume24hQuote, BreakoutAnalysis? Breakouts, bool HistoryLoaded);
