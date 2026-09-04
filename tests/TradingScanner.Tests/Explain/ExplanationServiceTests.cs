using System.Text.Json;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Explain;
using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Explain;

public class ExplanationServiceTests
{
    private sealed class FakeModel(string reply) : IExplanationModel
    {
        public int Calls;
        public string? LastSystem; public string? LastUser;
        public string ModelName => "fake-model";
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) { Calls++; LastSystem = system; LastUser = user; return Task.FromResult(reply); }
    }

    private static Opportunity Opp()
    {
        var r = Level("R", 1.40, 3);
        var setup = new SetupClassification(SetupType.BreakoutRetest, Confidence.High, TrendBias.Bullish, ["Retest held: reclaimed 1.4000"], Status(r, BreakoutState.RetestHeld), 1.40);
        var plan = new TradePlan(1.40, 1.406, "hold above 1.4000", 1.398, 1.397, 1.418, 1.43, 1.45, 0.006, 2.5, 4.5, 7.8, ["stop under the level"]);
        return new Opportunity(Xrp, T.Base, 1.406, 1, 92, new ScoreBreakdown([new ScoreComponent("Momentum", 16, 20, "5m +0.60%")], [], 92, 92, 1), setup, plan,
            new OverextensionAssessment(0.1, false, [], 0.9, 0.8, 0.5, 1.1, 0.02, 0.04, 1.2), ["Retest held"], "A close below 1.398 invalidates", [],
            new OpportunityMetrics(0.001, 0.006, 0.012, 0.02, 0.04, 0.002, 2.1, 0.6, 67, 61, 0.01, 0.007, 1.397, 0.006, 0.9, 1.418, 1.395, 3.2, 5e7, 0.7, "Uptrend", "Range", "Bullish", "Mixed"),
            null, new DataQuality(false, 100, true, "coinbase", "Coinbase Exchange"));
    }

    [Fact]
    public async Task Prompt_carries_only_engine_numbers_and_the_reply_is_parsed_and_cached()
    {
        var model = new FakeModel("""{"summary":"XRP reclaimed 1.4000 after a shallow retest on 2.1× volume.","why":["retest held","BTC bullish"],"invalidation":["close below 1.398"],"risks":["resistance 1.418"],"appearsExtended":false}""");
        var svc = new ExplanationService(model, new FixedTime(T.Base));
        Assert.True(svc.Enabled);
        var e = await svc.ExplainAsync(Opp(), Market(), default);

        Assert.Equal("XRP reclaimed 1.4000 after a shallow retest on 2.1× volume.", e.Summary);
        Assert.Equal(["retest held", "BTC bullish"], e.Why);
        Assert.Equal(["close below 1.398"], e.Invalidation);
        Assert.False(e.AppearsExtended);
        Assert.Equal("fake-model", e.Model);
        Assert.Equal(ExplanationService.DisclaimerText, e.Disclaimer);

        using var doc = JsonDocument.Parse(model.LastUser!);
        var root = doc.RootElement;
        Assert.Equal("XRP-USD", root.GetProperty("symbol").GetString());
        Assert.Equal(1.406, root.GetProperty("price").GetDouble());
        Assert.Equal("BreakoutRetest", root.GetProperty("setup").GetProperty("type").GetString());
        Assert.Equal(1.397, root.GetProperty("plan").GetProperty("stop").GetDouble());
        Assert.Equal("RiskOn", root.GetProperty("market").GetProperty("regime").GetString());
        Assert.Contains("never invent", model.LastSystem);
        Assert.Contains("guaranteed", model.LastSystem);

        await svc.ExplainAsync(Opp(), Market(), default);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Non_json_replies_are_kept_as_summary_and_disabled_service_is_explicit()
    {
        var svc = new ExplanationService(new FakeModel("Plain prose without JSON."), new FixedTime(T.Base));
        var e = await svc.ExplainAsync(Opp(), Market(), default);
        Assert.Equal("Plain prose without JSON.", e.Summary);
        Assert.Empty(e.Why);

        var off = new ExplanationService(null);
        Assert.False(off.Enabled);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => off.ExplainAsync(Opp(), Market(), default));
        Assert.Contains("ANTHROPIC_API_KEY", ex.Message);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
