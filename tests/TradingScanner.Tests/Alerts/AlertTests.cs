using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Core.Market;
using TradingScanner.Signals;
using TradingScanner.Signals.Alerts;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Alerts;

public class AlertEvaluatorTests
{
    private static AlertRule Rule(int hold = 0, int cooldown = 0, params AlertCondition[] conds) =>
        new(Guid.NewGuid(), "r", true, "XRP-USD", conds, hold, cooldown, ["browser"], null, T.Base, null);

    private static Dictionary<AlertField, AlertValue> V(double price, double relVol = 1.0, string btcTrend = "Bullish", bool aboveVwap = true) => new()
    {
        [AlertField.Price] = AlertValue.Of(price), [AlertField.RelVol] = AlertValue.Of(relVol), [AlertField.BtcTrend] = AlertValue.Of(btcTrend), [AlertField.AboveVwap] = AlertValue.Of(aboveVwap),
    };

    [Fact]
    public void Compound_conditions_must_all_hold()
    {
        var rule = Rule(0, 0, new AlertCondition(AlertField.Price, AlertOperator.Gt, "1.405"), new AlertCondition(AlertField.RelVol, AlertOperator.Gt, "1.5"), new AlertCondition(AlertField.BtcTrend, AlertOperator.Ne, "Bearish"));
        var s = new AlertState();
        Assert.False(AlertEvaluator.Evaluate(rule, V(1.41, 1.2), s, T.Base).Fire);
        Assert.False(AlertEvaluator.Evaluate(rule, V(1.41, 2.0, "Bearish"), s, T.Base).Fire);
        var d = AlertEvaluator.Evaluate(rule, V(1.41, 2.0), s, T.Base);
        Assert.True(d.Fire);
    }

    [Fact]
    public void Hold_time_rejects_a_single_tick_wick_and_fires_after_sustained_conditions()
    {
        var rule = Rule(hold: 180, cooldown: 0, new AlertCondition(AlertField.Price, AlertOperator.Gt, "1.405"));
        var s = new AlertState();
        Assert.False(AlertEvaluator.Evaluate(rule, V(1.41), s, T.Base).Fire);            // arms at t=0
        Assert.False(AlertEvaluator.Evaluate(rule, V(1.40), s, T.Base.AddSeconds(5)).Fire); // wick back below: disarmed
        Assert.Null(s.HeldSince);
        Assert.False(AlertEvaluator.Evaluate(rule, V(1.41), s, T.Base.AddSeconds(10)).Fire);
        var holding = AlertEvaluator.Evaluate(rule, V(1.412), s, T.Base.AddSeconds(100));
        Assert.True(holding.Holding);
        Assert.Contains("holding 90s of 180s", holding.Reason);
        Assert.False(holding.Fire);
        var fire = AlertEvaluator.Evaluate(rule, V(1.415), s, T.Base.AddSeconds(190));
        Assert.True(fire.Fire);
        Assert.Equal(180, fire.HeldFor!.Value.TotalSeconds);
    }

    [Fact]
    public void Repeating_rules_refire_after_the_cooldown_while_conditions_persist()
    {
        var rule = Rule(hold: 0, cooldown: 300, new AlertCondition(AlertField.Price, AlertOperator.Gt, "1.405")) with { RepeatWhileTrue = true };
        var s = new AlertState();
        Assert.True(AlertEvaluator.Evaluate(rule, V(1.41), s, T.Base).Fire);
        Assert.False(AlertEvaluator.Evaluate(rule, V(1.42), s, T.Base.AddSeconds(60)).Fire);
        Assert.Equal("in cooldown", AlertEvaluator.Evaluate(rule, V(1.42), s, T.Base.AddSeconds(120)).Reason);
        Assert.True(AlertEvaluator.Evaluate(rule, V(1.42), s, T.Base.AddSeconds(301)).Fire);
    }

    [Fact]
    public void Default_rules_are_edge_triggered_and_need_a_reset_before_refiring()
    {
        var rule = Rule(hold: 0, cooldown: 0, new AlertCondition(AlertField.Price, AlertOperator.Gt, "1.405"));
        var s = new AlertState();
        Assert.True(AlertEvaluator.Evaluate(rule, V(1.41), s, T.Base).Fire);
        Assert.Contains("already fired", AlertEvaluator.Evaluate(rule, V(1.42), s, T.Base.AddSeconds(1)).Reason);
        Assert.False(AlertEvaluator.Evaluate(rule, V(1.40), s, T.Base.AddSeconds(2)).Fire);
        Assert.True(AlertEvaluator.Evaluate(rule, V(1.41), s, T.Base.AddSeconds(3)).Fire);
    }

    [Fact]
    public void Crosses_above_requires_the_previous_value_below_and_then_holds_on_the_target_side()
    {
        var rule = Rule(hold: 60, cooldown: 0, new AlertCondition(AlertField.Price, AlertOperator.CrossesAbove, "0.612"));
        var s = new AlertState();
        Assert.Equal("waiting for a cross", AlertEvaluator.Evaluate(rule, V(0.62), s, T.Base).Reason); // already above: no cross
        Assert.False(AlertEvaluator.Evaluate(rule, V(0.60), s, T.Base.AddSeconds(10)).Fire);
        var armed = AlertEvaluator.Evaluate(rule, V(0.615), s, T.Base.AddSeconds(20));          // crossed
        Assert.True(armed.Holding);
        Assert.False(AlertEvaluator.Evaluate(rule, V(0.618), s, T.Base.AddSeconds(50)).Fire);      // still above, holding
        Assert.True(AlertEvaluator.Evaluate(rule, V(0.62), s, T.Base.AddSeconds(80)).Fire);        // held 60s
        Assert.False(AlertEvaluator.Evaluate(rule, V(0.63), s, T.Base.AddSeconds(90)).Fire);       // stays above: no new cross
    }

    [Fact]
    public void Text_and_boolean_fields_use_equality_and_validation_rejects_bad_rules()
    {
        var rule = Rule(0, 0, new AlertCondition(AlertField.AboveVwap, AlertOperator.Eq, "false"), new AlertCondition(AlertField.BtcTrend, AlertOperator.Eq, "bullish"));
        var s = new AlertState();
        Assert.False(AlertEvaluator.Evaluate(rule, V(1, aboveVwap: true), s, T.Base).Fire);
        Assert.True(AlertEvaluator.Evaluate(rule, V(1, aboveVwap: false), s, T.Base).Fire);

        Assert.Null(AlertEvaluator.Validate(rule));
        Assert.Contains("numeric", AlertEvaluator.Validate(Rule(0, 0, new AlertCondition(AlertField.Price, AlertOperator.Gt, "abc"))));
        Assert.Contains("condition", AlertEvaluator.Validate(Rule(0, 0)));
        Assert.Contains("true or false", AlertEvaluator.Validate(Rule(0, 0, new AlertCondition(AlertField.DoNotChase, AlertOperator.Eq, "yes"))));
        var webhook = Rule(0, 0, new AlertCondition(AlertField.Price, AlertOperator.Gt, "1")) with { Channels = ["webhook"], WebhookUrl = "http://insecure" };
        Assert.Contains("https", AlertEvaluator.Validate(webhook));
    }

    [Fact]
    public void Missing_values_never_fire()
    {
        var rule = Rule(0, 0, new AlertCondition(AlertField.Rsi5m, AlertOperator.Gt, "70"));
        var d = AlertEvaluator.Evaluate(rule, V(1.41), new AlertState(), T.Base);
        Assert.False(d.Fire);
        Assert.Contains("unavailable", d.Reason);
    }
}

public class AlertValuesTests
{
    [Fact]
    public void Values_are_extracted_from_the_opportunity_and_market()
    {
        var r = Level("R", 1.40, 3);
        var p = Proj(price: 1.412);
        var market = Market();
        var setup = new SetupClassification(SetupType.BreakoutRetest, Confidence.High, TrendBias.Bullish, [], Status(r, BreakoutState.RetestHeld), 1.40);
        var o = new Opportunity(Xrp, T.Base, 1.412, 3, 88, new ScoreBreakdown([], [], 88, 88, 1), setup, null,
            new OverextensionAssessment(0.1, false, [], null, null, null, null, null, null, null), [], "", [],
            new OpportunityMetrics(0.001, 0.006, 0.012, 0.02, 0.04, 0.002, 2.2, 0.6, 64, 61, 0.01, 0.007, 1.395, 0.012, 1.4, 1.44, 1.38, 3.2, 5e7, 0.7, "Uptrend", "Range", "Bullish", "Mixed"),
            null, new DataQuality(false, 100, true, "t", "t"));
        var v = AlertValues.From(o, market);
        Assert.Equal(1.412, v[AlertField.Price].Number);
        Assert.Equal(88, v[AlertField.Score].Number);
        Assert.Equal("RetestHeld", v[AlertField.BreakoutState].Text);
        Assert.Equal("BreakoutRetest", v[AlertField.Setup].Text);
        Assert.True(v[AlertField.AboveVwap].Flag);
        Assert.Equal("Bullish", v[AlertField.BtcTrend].Text);
        Assert.False(v[AlertField.BtcDumping].Flag);
        Assert.Equal((1.40 - 1.412) / 1.412, v[AlertField.KeyLevelDistancePct].Number!.Value, 9);
    }
}

public class AlertServiceTests
{
    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => new(new StubHttpHandler()); }

    private sealed class Stub : IAnalyticsReader, ISignalsReader, ISymbolInfoReader
    {
        public IReadOnlyCollection<Symbol> Symbols => [];
        public AnalyticsSnapshot? GetSnapshot(Symbol symbol) => null;
        public AnalyticsProjection? Project(Symbol symbol, double price, DateTimeOffset now) => null;
        public BreakoutAnalysis? GetBreakouts(Symbol symbol) => null;
        public PriceQuote? GetQuote(Symbol symbol) => null;
        public MarketStats? GetStats(Symbol symbol) => null;
        public bool IsHistoryLoaded(Symbol symbol) => false;
    }

    private static Opportunity Opp(string symbol, double price, double relVol, double score)
    {
        var setup = new SetupClassification(SetupType.None, Confidence.Low, TrendBias.Neutral, [], null, null);
        return new Opportunity(new Symbol(symbol), T.Base, price, 1, score, new ScoreBreakdown([], [], score, score, 1), setup, null,
            new OverextensionAssessment(0, false, [], null, null, null, null, null, null, null), [], "", [],
            new OpportunityMetrics(null, null, null, null, null, null, relVol, null, null, null, null, null, null, 0.001, null, null, null, null, null, null, null, null, null, null),
            null, new DataQuality(false, 0, true, "t", "t"));
    }

    private static ScannerSnapshot Snap(DateTimeOffset at, params Opportunity[] opps) => new(at, Market(), opps, opps.Length, 1);

    [Fact]
    public async Task Rules_fire_per_symbol_append_events_and_respect_symbol_scope()
    {
        var stub = new Stub();
        var scanner = new ScannerService(stub, stub, stub, Options.Create(new ScannerOptions()), NullLogger<ScannerService>.Instance);
        var repo = new InMemoryAlertRepository();
        await repo.UpsertRuleAsync(new AlertRule(Guid.NewGuid(), "Any score > 90", true, null, [new AlertCondition(AlertField.Score, AlertOperator.Gt, "90")], 0, 600, ["browser"], null, T.Base, null), default);
        await repo.UpsertRuleAsync(new AlertRule(Guid.NewGuid(), "XRP volume", true, "XRP-USD", [new AlertCondition(AlertField.RelVol, AlertOperator.Gte, "2"), new AlertCondition(AlertField.Price, AlertOperator.Gt, "1.405")], 0, 0, ["browser"], null, T.Base, null), default);
        var service = new AlertService(scanner, repo, new NoHttp(), NullLogger<AlertService>.Instance);
        await service.ReloadAsync(default);
        var fired = new List<AlertEvent>();
        service.Fired += fired.Add;

        var events = await service.EvaluateAsync(Snap(T.Base, Opp("XRP-USD", 1.41, 2.5, 95), Opp("ADA-USD", 0.21, 2.5, 92), Opp("SOL-USD", 150, 3, 40)), default);

        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.RuleName == "Any score > 90" && e.Symbol == "XRP-USD");
        Assert.Contains(events, e => e.RuleName == "Any score > 90" && e.Symbol == "ADA-USD");
        Assert.Contains(events, e => e.RuleName == "XRP volume" && e.Symbol == "XRP-USD" && e.Message.Contains("RelVol ≥ 2 (now 2.5)"));
        Assert.DoesNotContain(events, e => e.Symbol == "SOL-USD");
        Assert.Equal(3, fired.Count);
        Assert.Equal(3, (await repo.ListEventsAsync(10, default)).Count);
        Assert.NotNull((await repo.ListRulesAsync(default)).First(r => r.Name == "XRP volume").LastFiredAt);

        // Same snapshot again: the score rule is in cooldown, the XRP rule needs conditions to drop and re-arm.
        var again = await service.EvaluateAsync(Snap(T.Base.AddSeconds(1), Opp("XRP-USD", 1.41, 2.5, 95), Opp("ADA-USD", 0.21, 2.5, 92)), default);
        Assert.Empty(again);
    }
}
