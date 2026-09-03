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

        var market = MarketContextBuilder.Build(inputs.Select(i => i.Projection).ToList(), now);
        var btcReturns = inputs.FirstOrDefault(i => i.Symbol == MarketContextBuilder.Btc)?.Snapshot.Momentum?.RecentReturns;
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

    public Opportunity Evaluate(ScanInput input, MarketContext market, double[]? btcReturns, Opportunity? previous, DateTimeOffset now)
    {
        var p = input.Projection;
        var setup = SetupClassifier.Classify(p, input.Breakouts, market);
        var over = OverextensionAnalyzer.Assess(p, _o.Overextension);
        var plan = TradePlanBuilder.Build(setup, p);
        var spread = input.Quote.SpreadBps;
        var breakdown = OpportunityScorer.Score(p, setup, plan, over, input.Breakouts, market, double.IsNaN(spread) ? null : spread, input.Volume24hQuote, _o.Scoring);
        var metrics = BuildMetrics(input, btcReturns, spread);
        var why = BuildWhy(setup, breakdown, over);
        var invalidation = BuildInvalidation(setup, plan);
        var risks = BuildRisks(breakdown, over, market);
        var change = previous is null ? null : Diff(previous, breakdown);
        var age = now - input.Quote.ExchangeTime;
        var quality = new DataQuality(age > _o.StaleQuoteThreshold, (long)age.TotalMilliseconds, input.HistoryLoaded, input.Quote.Provider, input.Quote.Exchange);
        return new Opportunity(input.Symbol, p.AsOf, p.Price, 0, breakdown.Total, breakdown, setup, plan, over, why, invalidation, risks, metrics, change, quality);
    }

    private static OpportunityMetrics BuildMetrics(ScanInput input, double[]? btcReturns, double spread)
    {
        var p = input.Projection;
        var m5 = p.For(Timeframe.M5);
        var m15 = p.For(Timeframe.M15);
        var s5 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M5);
        var s15 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M15);
        var mom = p.Momentum;
        return new OpportunityMetrics(
            mom?.R1m, mom?.R5m, mom?.R15m, mom?.R1h, mom?.R24h, mom?.Accel5m,
            m5?.RelVolume, m5?.BuyShare, m5?.Rsi, m15?.Rsi, m5?.Atr, m5?.AtrPct,
            p.Vwap?.Vwap, p.Vwap?.DeviationPct, p.Vwap?.Sigma,
            s5?.NearestResistance?.Price, s5?.NearestSupport?.Price,
            double.IsNaN(spread) ? null : spread, input.Volume24hQuote,
            input.Symbol == MarketContextBuilder.Btc ? 1.0 : Correlation(input.Snapshot.Momentum?.RecentReturns, btcReturns),
            s5?.Trend.ToString(), s15?.Trend.ToString(), m5?.Alignment.ToString(), m15?.Alignment.ToString());
    }

    /// <summary>Pearson correlation of the overlapping tail of two return series; null below 20 observations.</summary>
    public static double? Correlation(double[]? a, double[]? b)
    {
        if (a is null || b is null) return null;
        var n = Math.Min(a.Length, b.Length);
        if (n < 20) return null;
        double sa = 0, sb = 0;
        for (var i = 0; i < n; i++) { sa += a[a.Length - n + i]; sb += b[b.Length - n + i]; }
        var ma = sa / n; var mb = sb / n;
        double cov = 0, va = 0, vb = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[a.Length - n + i] - ma; var db = b[b.Length - n + i] - mb;
            cov += da * db; va += da * da; vb += db * db;
        }
        if (va <= 0 || vb <= 0) return null;
        return Math.Round(cov / Math.Sqrt(va * vb), 3);
    }

    private static List<string> BuildWhy(SetupClassification setup, ScoreBreakdown b, OverextensionAssessment over)
    {
        var why = new List<string>();
        foreach (var e in setup.Evidence) why.Add(e);
        foreach (var c in b.Components.OrderByDescending(c => c.Points / Math.Max(1, c.Max)).Take(3))
            if (c.Points >= 0.3 * c.Max && !string.IsNullOrEmpty(c.Evidence) && c.Name != OpportunityScorer.Breakout) why.Add($"{c.Name}: {c.Evidence}");
        if (!over.DoNotChase && over.Score < 0.3 && setup.Type != SetupType.None) why.Add("Not extended: entry is still close to the level");
        return why;
    }

    private static string BuildInvalidation(SetupClassification setup, TradePlan? plan)
    {
        if (plan is null) return setup.Type == SetupType.None ? "No setup is active; nothing to invalidate." : "No plan could be built from the current structure.";
        var lvl = LevelBreakoutTracker.P(plan.Invalidation);
        return setup.Type switch
        {
            SetupType.Breakout or SetupType.RangeBreakout => $"A sustained 5m close below {lvl} would return price beneath the breakout level and invalidate the continuation thesis.",
            SetupType.BreakoutRetest => $"A 5m close below {lvl} would mean the retest failed and the breakout structure is lost.",
            SetupType.VwapReclaim => $"Losing VWAP again with a close below {lvl} invalidates the reclaim.",
            SetupType.SupportBounce => $"A close below {lvl} breaks the support that produced the bounce.",
            SetupType.TrendPullback => $"A close below {lvl} turns the pullback into a trend break.",
            SetupType.MomentumContinuation => $"A close below {lvl} means momentum has stalled under the EMA20 structure.",
            SetupType.Reversal => $"A new low below {lvl} negates the divergence.",
            _ => $"A close below {lvl} means the expansion failed to hold.",
        } + (plan.Basis.Count > 0 ? $" Stop {LevelBreakoutTracker.P(plan.Stop)}: {plan.Basis[0]}." : "");
    }

    private static List<string> BuildRisks(ScoreBreakdown b, OverextensionAssessment over, MarketContext market)
    {
        var risks = new List<string>();
        if (over.DoNotChase) risks.Add("DO NOT CHASE: " + (over.Flags.Count > 0 ? string.Join("; ", over.Flags) : "risk/reward has deteriorated"));
        foreach (var pnl in b.Penalties.OrderByDescending(x => x.Points)) if (pnl.Points >= 2) risks.Add($"{pnl.Name} −{pnl.Points:F0}: {pnl.Evidence}");
        if (!market.AltsFavorable) risks.Add($"Regime {market.Regime}: altcoin conditions do not favor aggressive entries");
        return risks;
    }

    private static ScoreChange? Diff(Opportunity previous, ScoreBreakdown current)
    {
        if (Math.Abs(previous.Score - current.Total) < 3) return null;
        var reasons = new List<string>();
        foreach (var c in current.Components)
        {
            var d = c.Points - previous.Breakdown.ComponentPoints(c.Name);
            if (Math.Abs(d) >= 2) reasons.Add($"{c.Name} {d:+0;-0}: {c.Evidence}");
        }
        foreach (var pn in current.Penalties)
        {
            var d = pn.Points - previous.Breakdown.PenaltyPoints(pn.Name);
            if (Math.Abs(d) >= 2) reasons.Add($"{pn.Name} penalty {d:+0;-0}: {pn.Evidence}");
        }
        foreach (var pp in previous.Breakdown.Penalties)
            if (current.PenaltyPoints(pp.Name) == 0 && pp.Points >= 2) reasons.Add($"{pp.Name} penalty cleared (was −{pp.Points:F0})");
        return new ScoreChange(previous.Score, current.Total, reasons);
    }
}

public sealed record ScanInput(Symbol Symbol, AnalyticsSnapshot Snapshot, AnalyticsProjection Projection, PriceQuote Quote, double? Volume24hQuote, BreakoutAnalysis? Breakouts, bool HistoryLoaded);
