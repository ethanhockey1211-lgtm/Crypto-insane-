using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Signals;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Scanner;
using TradingScanner.Signals.Tape;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Scanner;

public class ScannerServiceTests
{
    private sealed class Stub : IAnalyticsReader, ISignalsReader, ISymbolInfoReader
    {
        public Dictionary<Symbol, AnalyticsSnapshot> Analytics { get; } = new();
        public Dictionary<Symbol, BreakoutAnalysis> Breakouts { get; } = new();
        public Dictionary<Symbol, PriceQuote> Quotes { get; } = new();
        public IReadOnlyCollection<Symbol> Symbols => Quotes.Keys.ToArray();
        public AnalyticsSnapshot? GetSnapshot(Symbol symbol) => Analytics.GetValueOrDefault(symbol);
        public AnalyticsProjection? Project(Symbol symbol, double price, DateTimeOffset now) => Analytics.GetValueOrDefault(symbol)?.Project(price, now);
        public BreakoutAnalysis? GetBreakouts(Symbol symbol) => Breakouts.GetValueOrDefault(symbol);
        public PriceQuote? GetQuote(Symbol symbol) => Quotes.GetValueOrDefault(symbol);
        public MarketStats? GetStats(Symbol symbol) => new(1, 2, 0.5m, 20_000_000m, T.Base);
        public bool IsHistoryLoaded(Symbol symbol) => true;
    }

    private static AnalyticsSnapshot Snap(Symbol symbol, double price, double relVol = 1.5, double r5 = 0.005, EmaAlignment align = EmaAlignment.Bullish, params PriceLevel[] levels)
    {
        var refs = new Dictionary<int, double>();
        var prev = new Dictionary<int, double>();
        foreach (var h in new[] { 1, 3, 5, 15, 30, 60, 240 }) { refs[h] = price / (1 + r5 * h / 5.0); prev[h] = refs[h] / (1 + r5 * h / 10.0); }
        var returns = Enumerable.Range(0, 60).Select(i => (i % 2 == 0 ? 1 : -1) * 0.001 * (symbol.Value == "BTC-USD" ? 1 : 0.5)).ToArray();
        return new AnalyticsSnapshot(symbol, T.Base,
            [Ind(close: price, relVol: relVol, align: align), Ind(Timeframe.M15, close: price, align: align)],
            new VwapValues(T.Base, price * 0.995, price * 0.005, 300),
            new MomentumValues(price, refs, prev, returns),
            [Struct(close: price, levels: levels), Struct(Timeframe.M15, close: price)],
            null);
    }

    private static PriceQuote Quote(Symbol s, double price, DateTimeOffset at) =>
        new(s, (decimal)price, (decimal)(price * 0.9997), (decimal)(price * 1.0003), "test", "Test Exchange", at, at);

    private static ScannerService Service(Stub stub) =>
        new(stub, stub, stub, Options.Create(new ScannerOptions()), NullLogger<ScannerService>.Instance, new FixedTime(T.Base.AddSeconds(2)));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Fact]
    public void Cycle_ranks_the_universe_and_prefers_a_confirmed_breakout_over_a_bigger_but_extended_move()
    {
        var stub = new Stub();
        var btc = new Symbol("BTC-USD"); var xrp = new Symbol("XRP-USD"); var pump = new Symbol("PUMP-USD"); var dull = new Symbol("DULL-USD");
        var r = Level("R", 1.40, 3);
        stub.Quotes[btc] = Quote(btc, 60000, T.Base.AddSeconds(1)); stub.Analytics[btc] = Snap(btc, 60000);
        stub.Quotes[xrp] = Quote(xrp, 1.406, T.Base.AddSeconds(1)); stub.Analytics[xrp] = Snap(xrp, 1.406, relVol: 2.2, levels: [r, Level("R2", 1.44)]);
        stub.Breakouts[xrp] = Breakouts(Status(r, BreakoutState.Confirmed, barsSince: 1));
        stub.Quotes[pump] = Quote(pump, 2.0, T.Base.AddSeconds(1)); stub.Analytics[pump] = Snap(pump, 2.0, relVol: 5, r5: 0.05);
        stub.Quotes[dull] = Quote(dull, 5.0, T.Base.AddMinutes(-5)); stub.Analytics[dull] = Snap(dull, 5.0, relVol: 0.6, r5: -0.002, align: EmaAlignment.Bearish);

        var svc = Service(stub);
        var snap = svc.RunCycle();

        Assert.Equal(4, snap.Opportunities.Count);
        Assert.Equal("XRP-USD", snap.Opportunities[0].Symbol.Value);
        Assert.Equal(1, snap.Opportunities[0].Rank);
        Assert.Equal(SetupType.Breakout, snap.Opportunities[0].Setup.Type);
        Assert.NotNull(snap.Opportunities[0].Plan);
        var pumpOpp = snap.Get(pump)!;
        Assert.True(pumpOpp.Overextension.DoNotChase);
        Assert.True(pumpOpp.Score < snap.Opportunities[0].Score);
        var dullOpp = snap.Get(dull)!;
        Assert.True(dullOpp.Quality.Stale);
        Assert.Equal("DULL-USD", snap.Opportunities[^1].Symbol.Value);
        Assert.NotNull(snap.Market.Btc);
        Assert.Equal(1.0, snap.Get(btc)!.Metrics.BtcCorrelation);
        Assert.InRange(snap.Get(xrp)!.Metrics.BtcCorrelation!.Value, 0.99, 1.0);
        Assert.Same(snap, svc.Latest);
        Assert.Contains(snap.Opportunities[0].Why, w => w.Contains("Closed above 1.4000"));
        Assert.Contains("invalidate the continuation thesis", snap.Opportunities[0].Invalidation);
        Assert.Contains(pumpOpp.Risks, k => k.StartsWith("DO NOT CHASE"));
    }

    [Fact]
    public void Second_cycle_explains_score_changes_and_the_tape_records_transitions()
    {
        var stub = new Stub();
        var btc = new Symbol("BTC-USD"); var xrp = new Symbol("XRP-USD");
        var r = Level("R", 1.40, 3);
        stub.Quotes[btc] = Quote(btc, 60000, T.Base.AddSeconds(1)); stub.Analytics[btc] = Snap(btc, 60000);
        stub.Quotes[xrp] = Quote(xrp, 1.396, T.Base.AddSeconds(1)); stub.Analytics[xrp] = Snap(xrp, 1.396, relVol: 1.0, r5: 0.001, levels: [r, Level("R2", 1.44)]);
        stub.Breakouts[xrp] = Breakouts(Status(r, BreakoutState.Approaching, barsSince: 0, distance: -0.4, narrative: "0.40 ATR below 1.4000"));
        var svc = Service(stub);
        var first = svc.RunCycle();
        var before = first.Get(xrp)!;
        Assert.Equal(SetupType.None, before.Setup.Type);
        Assert.Null(before.Change);

        stub.Quotes[xrp] = Quote(xrp, 1.408, T.Base.AddSeconds(1));
        stub.Analytics[xrp] = Snap(xrp, 1.408, relVol: 2.4, r5: 0.006, levels: [r, Level("R2", 1.44)]);
        stub.Breakouts[xrp] = Breakouts(Status(r, BreakoutState.Confirmed, barsSince: 0));
        var second = svc.RunCycle();
        var after = second.Get(xrp)!;
        Assert.Equal(SetupType.Breakout, after.Setup.Type);
        Assert.True(after.Score > before.Score + 10);
        Assert.NotNull(after.Change);
        Assert.Equal(before.Score, after.Change!.Previous);
        Assert.Contains(after.Change.Reasons, x => x.StartsWith("Breakout +"));

        var tape = svc.Tape.Recent();
        Assert.Contains(tape, e => e.Kind == TapeEventKind.BreakoutConfirmed && e.Symbol == "XRP-USD");
        Assert.Contains(tape, e => e.Kind == TapeEventKind.SetupAppeared && e.Text.Contains("Breakout setup"));
        Assert.DoesNotContain(tape, e => e.Kind == TapeEventKind.RegimeChange);
    }

    [Fact]
    public void Correlation_is_pearson_over_the_overlapping_tail()
    {
        var a = Enumerable.Range(0, 60).Select(i => Math.Sin(i)).ToArray();
        var b = a.Select(x => 2 * x + 1).ToArray();
        var c = a.Select(x => -x).ToArray();
        Assert.Equal(1.0, OpportunityEvaluator.Correlation(a, b)!.Value, 6);
        Assert.Equal(-1.0, OpportunityEvaluator.Correlation(a, c)!.Value, 6);
        Assert.Null(OpportunityEvaluator.Correlation(a, new double[5]));
        Assert.Null(OpportunityEvaluator.Correlation(a, Enumerable.Repeat(0.0, 60).ToArray()));
    }
}
