using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TradingScanner.Analytics;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Candles;
using TradingScanner.Signals;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Performance;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Backtest;

/// <summary>
/// Replays 1m history through the SAME analytics, breakout, evaluation, and outcome code the live scanner uses.
/// Bars are fed in chronological close order across all symbols; at every 5m close the universe is evaluated
/// exactly as a live cycle would be, using only bars that had closed by then. Outcomes are advanced with the bars
/// that follow, entering at the open of the bar after the signal (latency), with the stop tested before targets.
/// </summary>
public sealed class BacktestRunner
{
    private readonly AnalyticsOptions _analytics;
    private readonly SignalsOptions _signals;
    private readonly ILogger<BacktestRunner> _logger;

    public BacktestRunner(AnalyticsOptions analytics, SignalsOptions signals, ILogger<BacktestRunner> logger)
    {
        _analytics = analytics;
        _signals = signals;
        _logger = logger;
    }

    private sealed class SymbolReplay
    {
        public required Symbol Symbol;
        public required SymbolAnalytics Analytics;
        public required SymbolBreakoutTracker Breakouts;
        public required List<Candle> M1;                 // ascending
        public int M1Cursor;                              // index of the next M1 bar not yet fed
        public Opportunity? Previous;
        public AnalyticsSnapshot? Snapshot;
        public BreakoutAnalysis? BreakoutState;
        public double LastClose;
        public readonly Dictionary<string, DateTimeOffset> LastRecorded = new(StringComparer.Ordinal);
        public readonly List<SignalWithOutcome> Active = new();
        public readonly List<SignalWithOutcome> Done = new();
    }

    public BacktestResult Run(IReadOnlyDictionary<Symbol, IReadOnlyList<Candle>> m1BySymbol, BacktestRequest request)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var horizon = request.Horizon ?? TimeSpan.FromHours(2);
        var dedupe = request.DedupeWindow ?? TimeSpan.FromMinutes(30);
        var scannerOptions = _signals.Scanner;
        var evalTf = _signals.Breakout.Timeframe;

        // Build every timeframe from 1m for each symbol and merge all bars into one chronological stream.
        var replays = new Dictionary<Symbol, SymbolReplay>();
        var stream = new List<(DateTimeOffset closeTime, Timeframe tf, Symbol symbol, Candle candle)>();
        foreach (var (symbol, m1raw) in m1BySymbol)
        {
            var m1 = m1raw.Where(c => c.Timeframe == Timeframe.M1).OrderBy(c => c.OpenTime).ToList();
            if (m1.Count == 0) { notes.Add($"{symbol}: no 1m bars"); continue; }
            replays[symbol] = new SymbolReplay { Symbol = symbol, Analytics = new SymbolAnalytics(symbol, _analytics), Breakouts = new SymbolBreakoutTracker(symbol, _signals.Breakout), M1 = m1 };
            foreach (var c in m1) stream.Add((c.CloseTime, Timeframe.M1, symbol, c));
            foreach (var tf in TimeframeExtensions.All)
            {
                if (tf == Timeframe.M1) continue;
                foreach (var c in TimeframeAggregator.AggregateAll(symbol, Timeframe.M1, tf, m1)) stream.Add((c.CloseTime, tf, symbol, c));
            }
        }
        stream.Sort((a, b) => a.closeTime != b.closeTime ? a.closeTime.CompareTo(b.closeTime) : a.tf.CompareTo(b.tf));
        if (!replays.ContainsKey(MarketContextBuilder.Btc)) notes.Add("BTC-USD not included: market regime and BTC alignment are evaluated without BTC context");
        notes.Add($"Breadth is computed over the {replays.Count} backtested symbol(s), not the live universe");
        notes.Add($"Entry at the open of the bar {request.Costs.LatencyBars} bar(s) after the signal; stop tested before targets within a bar (pessimistic)");

        var bars = 0; var evaluations = 0;
        var i = 0;
        while (i < stream.Count)
        {
            var t = stream[i].closeTime;
            var evalDue = false;
            // Feed everything that closed at t.
            while (i < stream.Count && stream[i].closeTime == t)
            {
                var (_, tf, symbol, candle) = stream[i++];
                var r = replays[symbol];
                r.Analytics.Update(candle);
                bars++;
                if (tf == Timeframe.M1)
                {
                    r.LastClose = (double)candle.Close;
                    r.M1Cursor++;
                    AdvanceOutcomes(r, candle, horizon);
                }
                var snap = r.Analytics.Snapshot();
                r.Snapshot = snap;
                if (tf == evalTf)
                {
                    var structure = snap.StructureFor(evalTf);
                    var ind = snap.For(evalTf);
                    if (structure is not null && ind is not null) r.BreakoutState = r.Breakouts.Update(candle, structure, ind);
                    evalDue = true;
                }
            }
            if (!evalDue || t < request.From || t > request.To) continue;

            // Evaluate the universe as of t, exactly like a live cycle.
            var inputs = new List<ScanInput>();
            foreach (var r in replays.Values)
            {
                if (r.Snapshot is null || r.LastClose <= 0) continue;
                var quote = new PriceQuote(r.Symbol, (decimal)r.LastClose, 0, 0, "backtest", "replay", t, t);
                inputs.Add(new ScanInput(r.Symbol, r.Snapshot, r.Snapshot.Project(r.LastClose, t), quote, null, r.BreakoutState, true));
            }
            var market = MarketContextBuilder.Build(inputs.Select(x => x.Projection).ToList(), t);
            var btcReturns = replays.TryGetValue(MarketContextBuilder.Btc, out var btc) ? btc.Snapshot?.Momentum?.RecentReturns : null;
            foreach (var input in inputs)
            {
                var r = replays[input.Symbol];
                var opp = OpportunityEvaluator.Evaluate(input, market, btcReturns, r.Previous, t, scannerOptions);
                r.Previous = opp;
                evaluations++;
                if (opp.Setup.Type == SetupType.None || opp.Score < request.RecordThreshold) continue;
                var key = opp.Setup.Type.ToString();
                if (r.LastRecorded.TryGetValue(key, out var last) && t - last < dedupe) continue;
                r.LastRecorded[key] = t;
                // Achievable entry: the open of the bar `LatencyBars` after the evaluation bar.
                var entryIdx = r.M1Cursor - 1 + Math.Max(0, request.Costs.LatencyBars);
                if (entryIdx >= r.M1.Count) continue;
                var record = SignalTracker.ToRecord(opp, market) with { At = t, Price = (double)r.M1[entryIdx].Open };
                var outcome = new SignalOutcome(record.Id, null, null, null, null, null, 0, 0, record.Stop is null ? null : false, record.Target1 is null ? null : false, record.Target2 is null ? null : false, record.Target3 is null ? null : false, "none", null, t, false);
                r.Active.Add(new SignalWithOutcome(record, outcome));
            }
        }

        var all = replays.Values.SelectMany(r => r.Done.Concat(r.Active)).OrderBy(x => x.Signal.At).ToList();
        var gross = SignalTracker.Report(all, request.To, scannerOptions.Scoring.Version);
        var net = SignalTracker.Report(all.Select(x => ApplyCosts(x, request.Costs)).ToList(), request.To, scannerOptions.Scoring.Version);
        _logger.LogInformation("Backtest: {Symbols} symbols, {Bars} bars, {Evals} evaluations, {Signals} signals in {Ms}ms", replays.Count, bars, evaluations, all.Count, sw.ElapsedMilliseconds);
        return new BacktestResult(request, all, gross, net, bars, evaluations, sw.Elapsed, notes);
    }

    private static void AdvanceOutcomes(SymbolReplay r, Candle m1, TimeSpan horizon)
    {
        if (r.Active.Count == 0) return;
        for (var k = r.Active.Count - 1; k >= 0; k--)
        {
            var item = r.Active[k];
            // Bars up to and including the latency bar are part of the entry, not the outcome.
            if (m1.OpenTime < item.Signal.At) continue;
            var updated = SignalTracker.AdvanceBar(item, (double)m1.High, (double)m1.Low, (double)m1.Close, m1.CloseTime, horizon);
            var next = item with { Outcome = updated };
            if (updated.Complete) { r.Active.RemoveAt(k); r.Done.Add(next); }
            else r.Active[k] = next;
        }
    }

    /// <summary>Net R = gross R minus round-trip costs expressed in units of initial risk.</summary>
    public static SignalWithOutcome ApplyCosts(SignalWithOutcome item, CostModel costs)
    {
        var s = item.Signal; var o = item.Outcome;
        if (o.R is not { } r || s.Entry is not { } entry || s.Stop is not { } stop || entry <= stop) return item;
        var riskPerUnit = entry - stop;
        var netR = r - costs.CostPerUnit(s.Price) / riskPerUnit;
        return item with { Outcome = o with { R = netR } };
    }
}
