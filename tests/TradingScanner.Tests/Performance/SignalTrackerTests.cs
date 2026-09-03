using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Performance;
using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Performance;

public class SignalTrackerTests
{
    private static Opportunity Opp(string symbol, double price, double score, SetupType setup, DateTimeOffset at, TradePlan? plan = null, bool stale = false)
    {
        var classification = new SetupClassification(setup, Confidence.Medium, TrendBias.Bullish, [], null, null);
        return new Opportunity(new Symbol(symbol), at, price, 1, score, new ScoreBreakdown([new ScoreComponent("Momentum", 15, 20, "x")], [], score, score, 3), classification, plan,
            new OverextensionAssessment(0, false, [], null, null, null, null, null, null, null), [], "", [],
            new OpportunityMetrics(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            null, new DataQuality(stale, 0, true, "t", "t"));
    }

    private static TradePlan Plan(double entry, double stop, double t1, double t2, double t3) =>
        new(entry, entry, "t", stop, stop, t1, t2, t3, entry - stop, (t1 - entry) / (entry - stop), (t2 - entry) / (entry - stop), (t3 - entry) / (entry - stop), []);

    private static ScannerSnapshot Snap(DateTimeOffset at, params Opportunity[] opps) => new(at, Market(), opps, opps.Length, 1);

    private static SignalTracker Tracker(InMemorySignalRepository repo) =>
        new(repo, Options.Create(new PerformanceOptions { RecordThreshold = 60, DedupeWindow = TimeSpan.FromMinutes(30), Horizon = TimeSpan.FromHours(1) }), NullLogger<SignalTracker>.Instance);

    [Fact]
    public async Task Records_qualifying_signals_once_per_dedupe_window_and_ignores_stale_or_weak_ones()
    {
        var repo = new InMemorySignalRepository();
        var tracker = Tracker(repo);
        var t0 = T.Base;
        var first = await tracker.ObserveAsync(Snap(t0,
            Opp("XRP-USD", 1.4, 72, SetupType.Breakout, t0, Plan(1.4, 1.39, 1.42, 1.43, 1.45)),
            Opp("ADA-USD", 0.2, 55, SetupType.Breakout, t0),
            Opp("SOL-USD", 150, 90, SetupType.None, t0),
            Opp("SUI-USD", 0.78, 80, SetupType.VwapReclaim, t0, stale: true)), default);
        Assert.Single(first);
        Assert.Equal("XRP-USD", first[0].Symbol);
        Assert.Equal("Breakout", first[0].Setup);
        Assert.Equal(1.39, first[0].Stop);
        Assert.Equal("RiskOn", first[0].Regime);

        var again = await tracker.ObserveAsync(Snap(t0.AddMinutes(10), Opp("XRP-USD", 1.41, 75, SetupType.Breakout, t0.AddMinutes(10))), default);
        Assert.Empty(again);
        var different = await tracker.ObserveAsync(Snap(t0.AddMinutes(11), Opp("XRP-USD", 1.41, 75, SetupType.BreakoutRetest, t0.AddMinutes(11))), default);
        Assert.Single(different);
        var later = await tracker.ObserveAsync(Snap(t0.AddMinutes(31), Opp("XRP-USD", 1.41, 75, SetupType.Breakout, t0.AddMinutes(31))), default);
        Assert.Single(later);
        Assert.Equal(3, (await repo.ListAsync(10, null, default)).Count);
    }

    [Fact]
    public async Task Outcome_follows_the_price_path_target_first_then_completes_at_the_horizon()
    {
        var repo = new InMemorySignalRepository();
        var tracker = Tracker(repo);
        var t0 = T.Base;
        await tracker.ObserveAsync(Snap(t0, Opp("XRP-USD", 1.40, 80, SetupType.Breakout, t0, Plan(1.40, 1.39, 1.42, 1.43, 1.45))), default);
        var path = new (int min, double px)[] { (1, 1.395), (5, 1.405), (12, 1.421), (15, 1.415), (30, 1.43), (45, 1.41), (60, 1.425) };
        foreach (var (min, px) in path) await tracker.ObserveAsync(Snap(t0.AddMinutes(min), Opp("XRP-USD", px, 70, SetupType.None, t0.AddMinutes(min))), default);

        var item = Assert.Single(await repo.ListAsync(10, "XRP-USD", default));
        var o = item.Outcome;
        Assert.True(o.Complete);
        Assert.Equal("t1", o.FirstEvent);
        Assert.True(o.Target1Hit);
        Assert.True(o.Target2Hit);
        Assert.False(o.Target3Hit);
        Assert.False(o.StopHit);
        Assert.Equal(1.405 / 1.40 - 1, o.Ret5m!.Value, 9);
        Assert.Equal(1.415 / 1.40 - 1, o.Ret15m!.Value, 9);
        Assert.Equal(1.43 / 1.40 - 1, o.Ret30m!.Value, 9);
        Assert.Equal(1.425 / 1.40 - 1, o.Ret1h!.Value, 9);
        Assert.Equal(1.43 / 1.40 - 1, o.Mfe, 9);
        Assert.Equal(1.395 / 1.40 - 1, o.Mae, 9);
        Assert.Equal(2.0, o.R!.Value, 9); // T1 before stop => +RR1
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public async Task Stop_first_is_minus_one_r_and_no_event_uses_the_one_hour_return()
    {
        var repo = new InMemorySignalRepository();
        var tracker = Tracker(repo);
        var t0 = T.Base;
        await tracker.ObserveAsync(Snap(t0,
            Opp("A-USD", 10, 80, SetupType.Breakout, t0, Plan(10, 9.5, 11, 12, 13)),
            Opp("B-USD", 10, 80, SetupType.Breakout, t0, Plan(10, 9.5, 11, 12, 13))), default);
        await tracker.ObserveAsync(Snap(t0.AddMinutes(3), Opp("A-USD", 9.4, 0, SetupType.None, t0.AddMinutes(3)), Opp("B-USD", 10.2, 0, SetupType.None, t0.AddMinutes(3))), default);
        await tracker.ObserveAsync(Snap(t0.AddMinutes(20), Opp("A-USD", 11.5, 0, SetupType.None, t0.AddMinutes(20)), Opp("B-USD", 10.1, 0, SetupType.None, t0.AddMinutes(20))), default);
        await tracker.ObserveAsync(Snap(t0.AddMinutes(60), Opp("A-USD", 12, 0, SetupType.None, t0.AddMinutes(60)), Opp("B-USD", 10.25, 0, SetupType.None, t0.AddMinutes(60))), default);

        var a = (await repo.ListAsync(10, "A-USD", default))[0].Outcome;
        Assert.Equal("stop", a.FirstEvent);
        Assert.True(a.Target1Hit); // hit later, but the stop came first
        Assert.Equal(-1, a.R);
        var b = (await repo.ListAsync(10, "B-USD", default))[0].Outcome;
        Assert.Equal("none", b.FirstEvent);
        Assert.Equal((10.25 - 10) / 0.5, b.R!.Value, 9);

        var report = SignalTracker.Report(await repo.ListAsync(100, null, default), t0.AddHours(2), 3);
        Assert.Equal(2, report.Overall.Signals);
        Assert.Equal(2, report.Overall.Completed);
        Assert.Equal(0.5, report.Overall.StopRate);
        Assert.Equal(0.0, report.Overall.TargetBeforeStopRate);
        Assert.Equal((-1 + 0.5) / 2, report.Overall.AvgR!.Value, 9);
        Assert.Equal(0.5, report.Overall.ProfitFactor!.Value, 9);
        Assert.Single(report.ByScoreBucket);
        Assert.Equal("80-89", report.ByScoreBucket[0].Key);
        Assert.Contains("too few", report.Note);
        Assert.Equal(3, report.ConfigVersion);
    }

    [Fact]
    public void Score_buckets_follow_the_brief()
    {
        Assert.Equal("90-100", SignalTracker.ScoreBucket(92));
        Assert.Equal("80-89", SignalTracker.ScoreBucket(80));
        Assert.Equal("70-79", SignalTracker.ScoreBucket(79.9));
        Assert.Equal("60-69", SignalTracker.ScoreBucket(60));
        Assert.Equal("<60", SignalTracker.ScoreBucket(59.9));
    }
}
