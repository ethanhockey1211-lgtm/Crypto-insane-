using Microsoft.Extensions.Logging.Abstractions;
using TradingScanner.Analytics;
using TradingScanner.Backtest;
using TradingScanner.Core.Market;
using TradingScanner.Signals;
using TradingScanner.Signals.Performance;
using TradingScanner.Tests.Analytics;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Backtest;

public class BacktestRunnerTests
{
    private static readonly Symbol Btc = new("BTC-USD");
    private static readonly Symbol Alt = new("ALT-USD");

    private static BacktestRunner Runner() => new(new AnalyticsOptions(), new SignalsOptions { Scanner = new() { SetupScoreThreshold = 60 } }, NullLogger<BacktestRunner>.Instance);

    /// <summary>A walk with a deliberate breakout: quiet range, then a volume-backed push and a retest.</summary>
    private static List<Candle> Scripted(Symbol symbol, int minutes, int seed)
    {
        var walk = SymbolAnalyticsTests.Walk(minutes, seed: seed, drift: 0.0);
        var list = new List<Candle>(walk.Count);
        for (var i = 0; i < walk.Count; i++)
        {
            var c = walk[i];
            var boost = i > 700 && i < 730 ? 1.006 : i >= 730 && i < 745 ? 0.9985 : i >= 745 && i < 780 ? 1.004 : 1.0;
            var vol = i > 700 && i < 730 ? 4m : i >= 745 && i < 780 ? 3m : 1m;
            var o = c.Open; var cl = c.Close * (decimal)Math.Pow(boost, 1.0);
            var h = Math.Max(o, cl) * 1.0005m; var l = Math.Min(o, cl) * 0.9995m;
            list.Add(new Candle(symbol, Timeframe.M1, c.OpenTime, o, h, l, cl, c.Volume * vol, c.QuoteVolume * vol, c.BuyVolume * vol, c.SellVolume * vol, c.TradeCount, CandleSource.Historical));
        }
        // re-chain opens so bars are continuous
        for (var i = 1; i < list.Count; i++) list[i] = list[i] with { Open = list[i - 1].Close, High = Math.Max(list[i].High, list[i - 1].Close), Low = Math.Min(list[i].Low, list[i - 1].Close) };
        return list;
    }

    [Fact]
    public void Replays_multiple_symbols_records_signals_and_produces_gross_and_net_reports()
    {
        var btc = SymbolAnalyticsTests.Walk(1200, seed: 21, drift: 0.0001, start: T.At("2024-03-01T00:00:00Z")).Select(c => c with { Symbol = Btc }).ToList();
        var alt = Scripted(Alt, 1200, seed: 4);
        var data = new Dictionary<Symbol, IReadOnlyList<Candle>> { [Btc] = btc, [Alt] = alt };
        var req = new BacktestRequest([Btc, Alt], T.At("2024-03-01T05:00:00Z"), T.At("2024-03-01T20:00:00Z"), new CostModel(10, 5, 4, 1), RecordThreshold: 0);

        var result = Runner().Run(data, req);

        Assert.True(result.BarsProcessed > 2400, $"bars {result.BarsProcessed}");
        Assert.True(result.Evaluations > 100, $"evaluations {result.Evaluations}");
        Assert.NotEmpty(result.Signals);
        Assert.All(result.Signals, s => Assert.InRange(s.Signal.At, req.From, req.To));
        Assert.All(result.Signals, s => Assert.Equal("backtest".Length > 0, s.Signal.Price > 0));
        Assert.Contains(result.Notes, n => n.Contains("pessimistic"));
        Assert.DoesNotContain(result.Notes, n => n.Contains("BTC-USD not included"));
        Assert.Equal(result.Gross.Overall.Signals, result.Net.Overall.Signals);
        // Costs can only lower R.
        for (var i = 0; i < result.Signals.Count; i++)
        {
            var g = result.Signals[i].Outcome.R;
            var n = BacktestRunner.ApplyCosts(result.Signals[i], req.Costs).Outcome.R;
            if (g is { } gr && n is { } nr) Assert.True(nr < gr);
        }
        var completed = result.Signals.Where(s => s.Outcome.Complete).ToList();
        Assert.NotEmpty(completed);
        Assert.All(completed, s => Assert.True(s.Outcome.LastPrice >= s.Signal.At.AddHours(1)));
    }

    [Fact]
    public void Signals_never_depend_on_future_bars()
    {
        var btc = SymbolAnalyticsTests.Walk(1000, seed: 21, drift: 0.0001, start: T.At("2024-03-01T00:00:00Z")).Select(c => c with { Symbol = Btc }).ToList();
        var alt = Scripted(Alt, 1000, seed: 4);
        var to = T.At("2024-03-01T12:00:00Z");
        var req = new BacktestRequest([Btc, Alt], T.At("2024-03-01T04:00:00Z"), to, new CostModel(), RecordThreshold: 0);

        var full = Runner().Run(new Dictionary<Symbol, IReadOnlyList<Candle>> { [Btc] = btc, [Alt] = alt }, req);
        var truncated = Runner().Run(new Dictionary<Symbol, IReadOnlyList<Candle>>
        {
            [Btc] = btc.Where(c => c.CloseTime <= to).ToList(),
            [Alt] = alt.Where(c => c.CloseTime <= to).ToList(),
        }, req);

        var a = full.Signals.Select(s => (s.Signal.Symbol, s.Signal.At, s.Signal.Setup, Math.Round(s.Signal.Score, 6), s.Signal.Price)).ToList();
        var b = truncated.Signals.Select(s => (s.Signal.Symbol, s.Signal.At, s.Signal.Setup, Math.Round(s.Signal.Score, 6), s.Signal.Price)).ToList();
        Assert.NotEmpty(a);
        // The truncated run may lack the very last signal's entry bar; everything it did record must match exactly.
        Assert.Equal(b, a.Take(b.Count));
        Assert.True(a.Count - b.Count <= 1);
    }

    [Fact]
    public void Same_bar_touches_count_as_a_stop_and_costs_reduce_r()
    {
        var signal = new SignalRecord(Guid.NewGuid(), "X-USD", T.Base, "Breakout", "High", 80, new Dictionary<string, double>(), 0, 100, 100, 98, 104, 106, 110, 2, "RiskOn", "Bullish", false, 1);
        var outcome = new SignalOutcome(signal.Id, null, null, null, null, null, 0, 0, false, false, false, false, "none", null, T.Base, false);
        var item = new SignalWithOutcome(signal, outcome);
        var bar = SignalTracker.AdvanceBar(item, high: 105, low: 97.5, close: 103, T.Base.AddMinutes(1), TimeSpan.FromHours(1));
        Assert.Equal("stop", bar.FirstEvent);
        Assert.True(bar.Target1Hit);
        Assert.Equal(-1, bar.R);
        Assert.Equal(0.05, bar.Mfe, 9);
        Assert.Equal(-0.025, bar.Mae, 9);

        var net = BacktestRunner.ApplyCosts(item with { Outcome = bar }, new CostModel(10, 5, 4, 1));
        // cost per unit = 100 × (20 + 5 + 2) / 10000 = 0.27; risk per unit = 2 → 0.135R
        Assert.Equal(-1.135, net.Outcome.R!.Value, 9);
    }
}
