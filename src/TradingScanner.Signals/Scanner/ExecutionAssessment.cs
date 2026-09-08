namespace TradingScanner.Signals.Scanner;

public sealed class ExecutionConfig
{
    // Per-side assumptions, not exchange fee quotes. Set these to your actual tier.
    public double FeeBps { get; set; } = 60;
    public double SlippageBps { get; set; } = 5;
    public double MinNetRewardRatio { get; set; } = 1.5;
    public double MaxSpreadBps { get; set; } = 20;
    public double MinVolume24hQuote { get; set; } = 2_000_000;
    /// <summary>
    /// Minimum evidence score for an execution-feasible setup. This deliberately matches
    /// <see cref="ScannerOptions.SetupScoreThreshold"/> so a setup is not recorded as active
    /// and then hidden from the decision workspace solely because of a second, stricter score gate.
    /// </summary>
    public double MinScore { get; set; } = 60;
}

public sealed record ExecutionAssessment(string Status, double? NetRewardRatio,
    double? BreakEvenWinRate, double? EntryCostPerUnit, double? NetRewardPerUnit,
    double? NetRiskPerUnit, IReadOnlyList<string> Reasons)
{
    public double? MaxEntryPriceAfterCosts { get; init; }
    /// <summary>Configured per-side assumptions, not fees or slippage observed on an order.</summary>
    public double? FeeBps { get; init; }
    public double? SlippageBps { get; init; }
}

/// <summary>Execution feasibility, NOT a prediction or estimated win probability.</summary>
public static class ExecutionAssessor
{
    public static ExecutionAssessment Assess(Opportunity o, MarketContext market, ExecutionConfig cfg) =>
        AssessCore(o, market, cfg) with
        {
            FeeBps = double.IsFinite(cfg.FeeBps) && cfg.FeeBps >= 0 ? cfg.FeeBps : null,
            SlippageBps = double.IsFinite(cfg.SlippageBps) && cfg.SlippageBps >= 0 ? cfg.SlippageBps : null,
        };

    private static ExecutionAssessment AssessCore(Opportunity o, MarketContext market, ExecutionConfig cfg)
    {
        var reasons = new List<string>();
        var spread = o.Metrics.SpreadBps;
        if (o.Quality.Stale || o.Quality.AgeMs < -2000) reasons.Add("Quote is stale or its timestamp is invalid");
        if (!o.Quality.HistoryLoaded) reasons.Add("History warm-up is incomplete");
        if (spread is null || !double.IsFinite(spread.Value) || spread < 0) reasons.Add("No valid two-sided spread available");
        else if (spread > cfg.MaxSpreadBps) reasons.Add("Spread exceeds the execution limit");
        if (o.Metrics.Volume24hQuote is not { } volume || !double.IsFinite(volume) || volume < cfg.MinVolume24hQuote)
            reasons.Add("Insufficient verified 24h quote volume");
        if (market.Btc is null) reasons.Add("BTC market context unavailable");
        if (market.Btc?.Dumping == true || market.Regime == MarketRegime.StrongRiskOff) reasons.Add("Market risk-off veto");
        if (o.Overextension.DoNotChase) reasons.Add("Overextended: do not chase");
        if (!double.IsFinite(o.Score) || o.Score < cfg.MinScore) reasons.Add("Evidence score below the execution threshold");
        if (o.Setup.Type == SetupType.None || o.Setup.Bias != TrendBias.Bullish) reasons.Add("No active bullish setup");
        var p = o.Plan;
        if (p is null || !double.IsFinite(o.Price) || !double.IsFinite(p.Stop) || !double.IsFinite(p.Target1)
            || p.Stop <= 0 || o.Price <= p.Stop || p.Target1 <= o.Price)
        {
            reasons.Add("No valid long trade geometry at the current price");
            return new("Blocked", null, null, null, null, null, reasons);
        }
        if (!double.IsFinite(cfg.FeeBps) || cfg.FeeBps < 0 || !double.IsFinite(cfg.SlippageBps) || cfg.SlippageBps < 0
            || !double.IsFinite(cfg.MinNetRewardRatio) || cfg.MinNetRewardRatio <= 0
            || !double.IsFinite(cfg.MaxSpreadBps) || cfg.MaxSpreadBps < 0
            || !double.IsFinite(cfg.MinVolume24hQuote) || cfg.MinVolume24hQuote < 0
            || !double.IsFinite(cfg.MinScore) || cfg.MinScore < 0 || cfg.MinScore > 100)
        {
            reasons.Add("Invalid execution cost configuration");
            return new("Blocked", null, null, null, null, null, reasons);
        }
        if (spread is null || !double.IsFinite(spread.Value) || spread < 0)
            return new("Blocked", null, null, null, null, null, reasons);
        var rate = (cfg.FeeBps + cfg.SlippageBps + spread.Value / 2) / 10_000;
        if (!double.IsFinite(rate) || rate >= 1)
        {
            reasons.Add("Execution cost assumption is out of range");
            return new("Blocked", null, null, null, null, null, reasons);
        }
        var entryCost = o.Price * rate;
        var reward = p.Target1 - o.Price - entryCost - p.Target1 * rate;
        var risk = o.Price - p.Stop + entryCost + p.Stop * rate;
        var rr = reward / risk;
        if (!double.IsFinite(reward) || !double.IsFinite(risk) || !double.IsFinite(rr) || risk <= 0)
        {
            reasons.Add("Execution arithmetic is out of range");
            return new("Blocked", null, null, null, null, null, reasons);
        }
        if (rr < cfg.MinNetRewardRatio) reasons.Add("Reward to T1 after estimated round-trip costs is insufficient");
        // Late means the price is above the preferred zone but still below the cost-aware chase
        // ceiling. It is a wait-for-pullback condition, not an execution veto. Chase remains a
        // hard block because the plan no longer has the configured reward remaining to T1.
        if (p.EntryState is EntryState.Chase) reasons.Add("Current price is above the no-chase ceiling");
        var status = reasons.Count > 0 ? "Blocked" : "Watch";
        if (reasons.Count == 0)
            reasons.Add(p.EntryState == EntryState.Late
                ? "Price is above the planned entry zone; wait for a pullback or a fresh setup before a paper entry"
                : "Cost and data checks pass; verify the stated confirmation trigger before a paper entry");
        var ceiling = (1 - rate) * (p.Target1 + cfg.MinNetRewardRatio * p.Stop)
            / ((1 + rate) * (1 + cfg.MinNetRewardRatio));
        return new(status, rr, reward > 0 ? risk / (risk + reward) : null, entryCost, reward, risk, reasons)
        { MaxEntryPriceAfterCosts = double.IsFinite(ceiling) ? ceiling : null };
    }
}
