using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;

namespace TradingScanner.Signals.Scanner;

/// <summary>Deterministic 0–100 score from weighted components minus penalties. Every number carries its evidence.</summary>
public static class OpportunityScorer
{
    public const string Momentum = "Momentum", Volume = "Volume", Structure = "Structure", Breakout = "Breakout", Market = "Market", Liquidity = "Liquidity", RiskReward = "RiskReward";
    public const string Overextension = "Overextension", WeakVolume = "WeakVolume", NearbyResistance = "NearbyResistance", BtcDisagreement = "BtcDisagreement", Spread = "Spread", FailedBreakout = "FailedBreakout";

    public static ScoreBreakdown Score(
        AnalyticsProjection p,
        SetupClassification setup,
        TradePlan? plan,
        OverextensionAssessment over,
        BreakoutAnalysis? breakouts,
        MarketContext market,
        double? spreadBps,
        double? volume24hQuote,
        ScoringConfig cfg)
    {
        var m5 = p.For(Timeframe.M5);
        var m15 = p.For(Timeframe.M15);
        var s5 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M5);
        var s15 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M15);
        var mom = p.Momentum;
        var price = p.Price;
        var atr = m5?.Atr is { } a && a > 0 ? a : price * 0.005;
        var components = new List<ScoreComponent>(7);
        var penalties = new List<ScoreComponent>(6);

        // Momentum
        {
            var f = 0.0; var ev = new List<string>();
            if (mom?.R5m is { } r5) { f += 0.4 * Ramp(r5, 0, 0.01); ev.Add($"5m {Pct(r5)}"); }
            if (mom?.R15m is { } r15) { f += 0.3 * Ramp(r15, 0, 0.02); ev.Add($"15m {Pct(r15)}"); }
            if (mom?.Accel5m is > 0) { f += 0.15; ev.Add("accelerating"); }
            if (m5?.Rsi is { } rsi) { f += rsi is >= 55 and <= 72 ? 0.15 : rsi is > 72 and <= 78 ? 0.07 : 0; ev.Add($"RSI5 {rsi:F0}"); }
            components.Add(new ScoreComponent(Momentum, Round(Math.Clamp(f, 0, 1) * cfg.MomentumMax), cfg.MomentumMax, string.Join(", ", ev)));
        }
        // Volume
        {
            var f = 0.0; var ev = new List<string>();
            if (m5?.RelVolume is { } rv) { f += 0.6 * Ramp(rv, cfg.RelVolNone, cfg.RelVolFull); ev.Add($"5m relative volume {rv:F1}×"); }
            if (m5?.VolumeFastRatio is >= 1.3) { f += 0.15; ev.Add("volume rising over the last 3 bars"); }
            if (m5?.BuyShare is { } bs) { f += 0.25 * Ramp(bs, 0.5, 0.7); ev.Add($"taker buys {bs:P0} of volume"); }
            components.Add(new ScoreComponent(Volume, Round(Math.Clamp(f, 0, 1) * cfg.VolumeMax), cfg.VolumeMax, string.Join(", ", ev)));
        }
        // Structure
        {
            var f = 0.0; var ev = new List<string>();
            f += TrendCredit(s5?.Trend, ev, "5m"); f += TrendCredit(s15?.Trend, ev, "15m");
            if (p.Vwap is { } v) { if (v.Above) { f += 0.2; ev.Add("above VWAP"); } else ev.Add("below VWAP"); }
            if (s5?.NearestResistance is { } res) { var room = (res.Price - price) / atr; f += 0.3 * Ramp(room, 0.5, 3); ev.Add($"next resistance {LevelBreakoutTracker.P(res.Price)} ({room:F1} ATR away)"); }
            else if (s5 is not null) { f += 0.3; ev.Add("no overhead resistance in the 5m structure"); }
            components.Add(new ScoreComponent(Structure, Round(Math.Clamp(f, 0, 1) * cfg.StructureMax), cfg.StructureMax, string.Join(", ", ev)));
        }
        // Breakout / retest quality
        {
            var f = 0.0; var ev = "no breakout in progress";
            if (breakouts?.BestUp is { } b)
            {
                f = b.State switch
                {
                    BreakoutState.RetestHeld => 1.0,
                    BreakoutState.Confirmed => b.WeakVolume ? 0.4 : b.BarsSinceBreakout <= 3 ? 0.85 : 0.6,
                    BreakoutState.Retesting => b.Retest?.PenetrationAtr is > 0.15 ? 0.35 : 0.5,
                    BreakoutState.Attempt => 0.2,
                    BreakoutState.Approaching => 0.3,
                    _ => 0,
                };
                if (b.State == BreakoutState.RetestHeld && b.Retest?.RecoveryRelVol is >= 1.5) f = 1.0;
                if (b.State == BreakoutState.Confirmed && b.BreakoutCloseStrength is >= 0.8 && b.BreakoutRelVol is >= 2.0) f = Math.Min(1, f + 0.1);
                ev = $"{b.State}: {b.Narrative}";
            }
            components.Add(new ScoreComponent(Breakout, Round(f * cfg.BreakoutMax), cfg.BreakoutMax, ev));
        }
        // Market alignment
        {
            var f = 0.5; var ev = "BTC context unavailable";
            if (market.Btc is { } btc)
            {
                f = btc.Dumping ? 0 : btc.Trend switch { TrendBias.Bullish => 1.0, TrendBias.Neutral => 0.5, _ => 0.15 };
                if (!btc.Dumping && btc.AboveVwap == true && btc.Trend != TrendBias.Bearish) f = Math.Min(1, f + 0.1);
                ev = btc.Summary + $"; regime {market.Regime}";
            }
            components.Add(new ScoreComponent(Market, Round(f * cfg.MarketMax), cfg.MarketMax, ev));
        }
        // Liquidity
        {
            var f = 0.0; var ev = new List<string>();
            if (volume24hQuote is { } vol && vol > 0) { f += 0.6 * Ramp(Math.Log10(vol), Math.Log10(cfg.LiquidityVolumeLow), Math.Log10(cfg.LiquidityVolumeHigh)); ev.Add($"24h volume ${vol / 1e6:F1}M"); }
            if (spreadBps is { } sp && !double.IsNaN(sp)) { f += 0.4 * (1 - Ramp(sp, cfg.SpreadBpsFull, cfg.SpreadBpsNone)); ev.Add($"spread {sp:F1} bps"); }
            components.Add(new ScoreComponent(Liquidity, Round(Math.Clamp(f, 0, 1) * cfg.LiquidityMax), cfg.LiquidityMax, string.Join(", ", ev)));
        }
        // Risk / reward
        {
            var f = 0.0; var ev = "no trade plan";
            if (plan is not null) { f = Ramp(plan.RewardRatio1, cfg.RrNone, cfg.RrFull); ev = $"{plan.RewardRatio1:F1}R to T1 {LevelBreakoutTracker.P(plan.Target1)} with stop {LevelBreakoutTracker.P(plan.Stop)}"; }
            components.Add(new ScoreComponent(RiskReward, Round(f * cfg.RiskRewardMax), cfg.RiskRewardMax, ev));
        }

        // Penalties
        if (over.Score > 0.05) penalties.Add(new ScoreComponent(Overextension, Round(over.Score * cfg.OverextensionPenaltyMax), cfg.OverextensionPenaltyMax, over.Flags.Count > 0 ? string.Join("; ", over.Flags) : $"extension score {over.Score:F2}"));
        if (m5?.RelVolume is { } rvp)
        {
            if (rvp < 1.0) penalties.Add(new ScoreComponent(WeakVolume, Round((1 - rvp) * cfg.WeakVolumePenaltyMax), cfg.WeakVolumePenaltyMax, $"5m volume only {rvp:F2}× baseline"));
        }
        else penalties.Add(new ScoreComponent(WeakVolume, Round(0.3 * cfg.WeakVolumePenaltyMax), cfg.WeakVolumePenaltyMax, "volume baseline not ready"));
        if (s5?.NearestResistance is { } nr)
        {
            var dist = (nr.Price - price) / atr;
            if (dist < cfg.NearbyResistanceAtr && dist > 0)
                penalties.Add(new ScoreComponent(NearbyResistance, Round((1 - dist / cfg.NearbyResistanceAtr) * cfg.NearbyResistancePenaltyMax), cfg.NearbyResistancePenaltyMax, $"resistance {LevelBreakoutTracker.P(nr.Price)} only {dist:F2} ATR above ({nr.Touches} touches)"));
        }
        if (market.Btc is { } b2)
        {
            if (b2.Dumping) penalties.Add(new ScoreComponent(BtcDisagreement, cfg.BtcDisagreementPenaltyMax, cfg.BtcDisagreementPenaltyMax, "BTC is selling off right now"));
            else if (b2.Trend == TrendBias.Bearish) penalties.Add(new ScoreComponent(BtcDisagreement, Round(0.5 * cfg.BtcDisagreementPenaltyMax), cfg.BtcDisagreementPenaltyMax, "BTC trend is bearish"));
            else if (b2.AboveVwap == false && setup.Type != SetupType.None) penalties.Add(new ScoreComponent(BtcDisagreement, Round(0.25 * cfg.BtcDisagreementPenaltyMax), cfg.BtcDisagreementPenaltyMax, "BTC is below its session VWAP"));
        }
        if (spreadBps is { } sp2 && !double.IsNaN(sp2) && sp2 > cfg.SpreadBpsFull)
            penalties.Add(new ScoreComponent(Spread, Round(Ramp(sp2, cfg.SpreadBpsFull, cfg.SpreadBpsNone) * cfg.SpreadPenaltyMax), cfg.SpreadPenaltyMax, $"spread {sp2:F1} bps"));
        if (breakouts?.BestUp is { State: BreakoutState.Failed } fb)
            penalties.Add(new ScoreComponent(FailedBreakout, Round(Math.Max(0.25, 1 - fb.BarsInState / 12.0) * cfg.FailedBreakoutPenaltyMax), cfg.FailedBreakoutPenaltyMax, fb.Narrative));
        if (setup.Bias == TrendBias.Bearish && setup.Type != SetupType.Reversal)
            penalties.Add(new ScoreComponent(Structure + "Bearish", Round(0.5 * cfg.StructureMax), cfg.StructureMax, "5m/15m structure is bearish"));

        var raw = components.Sum(c => c.Points);
        var total = Math.Clamp(raw - penalties.Sum(x => x.Points), 0, 100);
        return new ScoreBreakdown(components, penalties, Round(raw), Round(total), cfg.Version);
    }

    private static double TrendCredit(StructureTrend? t, List<string> ev, string tf)
    {
        switch (t)
        {
            case StructureTrend.Uptrend: ev.Add($"{tf} higher highs/lows"); return 0.25;
            case StructureTrend.Range: ev.Add($"{tf} ranging"); return 0.1;
            case StructureTrend.Downtrend: ev.Add($"{tf} lower highs/lows"); return 0;
            default: return 0.05;
        }
    }

    private static double Ramp(double x, double from, double to) => OverextensionAnalyzer.Ramp(x, from, to);
    private static double Round(double v) => Math.Round(v, 1);
    private static string Pct(double v) => MarketContextBuilder.Pct(v);
}
