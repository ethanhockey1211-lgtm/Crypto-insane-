using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;

namespace TradingScanner.Signals.Scanner;

/// <summary>
/// Finds setup candidates from deterministic rules, in priority order. Every classification carries
/// the evidence that produced it. Long-biased by design (the product hunts asymmetric long setups); bearish
/// structure is reported through <see cref="TrendBias"/> so the scorer can penalize.
/// </summary>
public static class SetupClassifier
{
    public static SetupClassification Classify(AnalyticsProjection p, BreakoutAnalysis? breakouts, MarketContext market) =>
        ClassifyCandidates(p, breakouts, market)[0];

    /// <summary>
    /// Retains every matching pattern and level so an unusable preferred plan cannot hide another
    /// independently confirmed setup. The evaluator still applies every execution check to each candidate.
    /// </summary>
    public static IReadOnlyList<SetupClassification> ClassifyCandidates(AnalyticsProjection p, BreakoutAnalysis? breakouts, MarketContext market)
    {
        var m5 = p.For(Timeframe.M5);
        var m15 = p.For(Timeframe.M15);
        var s5 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M5);
        var s15 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M15);
        var mom = p.Momentum;
        var price = p.Price;
        var atr = m5?.Atr ?? price * 0.005;
        var bias = Bias(m5, m15, s15, p.Vwap);
        var bullishBar = m5 is { Open: { } o } && m5.Close > o;
        var candidates = new List<SetupClassification>();
        // BestUp is ranked by tracker state and distance, not by this classifier's age
        // limits. An expired held retest must not hide a fresh break at another level.
        var active = ActiveBreakouts(breakouts, price).ToList();
        var up = active.FirstOrDefault() ?? breakouts?.BestUp;

        // 1. Breakout + retest held
        foreach (var held in active.Where(b => b.State == BreakoutState.RetestHeld))
        {
            var evidence = new List<string> { held.Narrative };
            if (held.BreakoutRelVol is { } rv) evidence.Add($"breakout bar volume {rv:F1}×");
            candidates.Add(Done(SetupType.BreakoutRetest, evidence, held, held.Level.Price, bias, market, m5, p, extra: held.Retest?.RecoveryRelVol is >= 1.2 ? 1 : 0));
        }

        // 2. Fresh confirmed breakout (range breakout when the level is the lookback range high)
        foreach (var confirmed in active.Where(b => b.State == BreakoutState.Confirmed))
        {
            var evidence = new List<string> { confirmed.Narrative };
            var isRange = (s5?.RangeHigh is { } rh && Math.Abs(rh - confirmed.Level.Price) <= 0.5 * atr) || (s15?.RangeHigh is { } rh15 && Math.Abs(rh15 - confirmed.Level.Price) <= 0.5 * atr);
            if (isRange) evidence.Add("level is the lookback range high");
            var type = isRange ? SetupType.RangeBreakout : SetupType.Breakout;
            candidates.Add(Done(type, evidence, confirmed, confirmed.Level.Price, bias, market, m5, p, extra: confirmed.WeakVolume ? -1 : 1));
        }

        // 3. VWAP reclaim
        if (p.VwapState is { Above: true, LastCross: CrossDirection.Bullish, BarsOnCurrentSide: <= 5, BarsOnPreviousSide: >= 10 } vs && p.Vwap is { } vw)
        {
            var volOk = vs.CrossRelVol is >= 1.2 || m5?.RelVolume is >= 1.2;
            if (volOk)
            {
                var evidence = new List<string>();
                evidence.Add($"reclaimed session VWAP {LevelBreakoutTracker.P(vw.Vwap)} {vs.BarsOnCurrentSide} bar(s) ago after {vs.BarsOnPreviousSide} bars below");
                if (vs.CrossRelVol is { } cv) evidence.Add($"reclaim bar volume {cv:F1}×");
                candidates.Add(Done(SetupType.VwapReclaim, evidence, null, vw.Vwap, bias, market, m5, p, extra: 0));
            }
        }

        // 4. Support bounce: a wick below support that closed back above it (Down-direction tracker in Attempt), or a bullish hold at support.
        if (breakouts is not null)
        {
            foreach (var st in breakouts.Levels)
            {
                if (st.Direction != BreakoutDirection.Down) continue;
                var bounced = st.State == BreakoutState.Attempt && st.DistanceAtr < 0 && st.BarsInState <= 2 && bullishBar;
                var held = st.State == BreakoutState.Approaching && bullishBar && st.BarsInState <= 1 && m5?.Rsi is > 35;
                if (!bounced && !held) continue;
                var evidence = new List<string>();
                evidence.Add(bounced ? $"dipped below support {LevelBreakoutTracker.P(st.Level.Price)} and closed back above ({st.Level.Touches} touches)" : $"held support {LevelBreakoutTracker.P(st.Level.Price)} with a bullish 5m close ({st.Level.Touches} touches)");
                if (m5?.RelVolume is { } rv) evidence.Add($"5m volume {rv:F1}×");
                candidates.Add(Done(SetupType.SupportBounce, evidence, st, st.Level.Price, bias, market, m5, p, extra: st.Level.Touches >= 3 ? 1 : 0));
            }
        }

        // 5. Reversal on bullish RSI divergence
        if (s5?.LatestDivergence is { Type: DivergenceType.BullishRsi } div && s5.BarIndex - div.BarIndex <= 8 && s5.Trend != StructureTrend.Uptrend && (bullishBar || (m5?.Ema9 is { } e9 && price > e9)))
        {
            var evidence = new List<string>();
            evidence.Add($"bullish RSI divergence: price {LevelBreakoutTracker.P(div.PreviousPrice)}→{LevelBreakoutTracker.P(div.Price)} while RSI {div.PreviousRsi:F0}→{div.Rsi:F0}");
            if (bullishBar) evidence.Add("latest 5m bar closed up");
            candidates.Add(Done(SetupType.Reversal, evidence, null, div.Price, bias, market, m5, p, extra: -1));
        }

        // 6. Volatility expansion out of compression
        if (m5 is { RangeRatio: >= 2.0, AtrRatio: <= 1.0 } && bullishBar)
        {
            var evidence = new List<string>();
            evidence.Add($"5m range {m5.RangeRatio:F1}× ATR after compression (fast/slow ATR {m5.AtrRatio:F2})");
            candidates.Add(Done(SetupType.VolatilityExpansion, evidence, null, m5.High, bias, market, m5, p, extra: 0));
        }

        // 7. Trend pullback to the 5m EMA20 inside a 15m uptrend
        if (m15 is { Alignment: EmaAlignment.Bullish } && m5 is { DistanceToEma20Atr: { } dist, Rsi: { } rsi } && dist is >= -0.6 and <= 0.4 && rsi is >= 38 and <= 58 && (mom?.Accel5m is > 0 || bullishBar) && (p.Vwap?.Sigma is null or >= -0.5))
        {
            var evidence = new List<string>();
            evidence.Add($"15m EMAs stacked bullish; price {dist:+0.00;-0.00} ATR from 5m EMA20 with RSI {rsi:F0}");
            if (mom?.Accel5m is > 0) evidence.Add("5m momentum turning up");
            candidates.Add(Done(SetupType.TrendPullback, evidence, null, m5.Ema20, bias, market, m5, p, extra: s15?.Trend == StructureTrend.Uptrend ? 1 : 0));
        }

        // 8. Momentum continuation
        if (m5 is { Alignment: EmaAlignment.Bullish, Rsi: < 75 } && p.Vwap?.Above == true && mom is { R15m: > 0, Accel5m: > 0 } && m5.RelVolume is >= 1.2)
        {
            var evidence = new List<string>();
            evidence.Add($"5m EMAs stacked bullish, above VWAP, {MarketContextBuilder.Pct(mom.R15m)} in 15m and accelerating");
            evidence.Add($"5m volume {m5.RelVolume:F1}×");
            candidates.Add(Done(SetupType.MomentumContinuation, evidence, null, m5.Ema20, bias, market, m5, p, extra: m15?.Alignment == EmaAlignment.Bullish ? 1 : 0));
        }

        // 9. Volume expansion without another structure
        if (m5 is { RelVolume: >= 2.5 } && bullishBar)
        {
            var evidence = new List<string>();
            evidence.Add($"5m volume {m5.RelVolume:F1}× baseline on an up bar");
            candidates.Add(Done(SetupType.VolumeExpansion, evidence, null, m5.Low, bias, market, m5, p, extra: -1));
        }

        if (candidates.Count > 0) return candidates;
        var idleEvidence = new List<string>();
        if (breakouts?.BestDown is { State: BreakoutState.Confirmed or BreakoutState.RetestHeld } dn) idleEvidence.Add($"breakdown in progress: {dn.Narrative}");
        else if (up is { State: BreakoutState.Approaching }) idleEvidence.Add(up.Narrative);
        else if (up is { State: BreakoutState.Failed }) idleEvidence.Add(up.Narrative);
        return [new SetupClassification(SetupType.None, Confidence.Low, bias, idleEvidence, up, up?.Level.Price)];
    }

    private static IEnumerable<BreakoutStatus> ActiveBreakouts(BreakoutAnalysis? breakouts, double price)
    {
        if (breakouts is null) return [];
        return breakouts.Levels
            .Where(b => b.Direction == BreakoutDirection.Up
                && (b is { State: BreakoutState.RetestHeld, BarsSinceBreakout: >= 0 and <= 12 }
                    || b is { State: BreakoutState.Confirmed, BarsSinceBreakout: >= 0 and <= 4 }))
            .OrderBy(b => b.State == BreakoutState.RetestHeld ? 0 : 1)
            .ThenBy(b => Math.Abs(price - b.Level.Price));
    }

    private static SetupClassification Done(SetupType type, List<string> evidence, BreakoutStatus? breakout, double? keyLevel, TrendBias bias, MarketContext market, IndicatorValues? m5, AnalyticsProjection p, int extra)
    {
        var confirmations = extra;
        if (m5?.RelVolume is >= 1.5) confirmations++;
        if (market.Btc?.Trend == TrendBias.Bullish && market.Btc.Dumping == false) confirmations++;
        if (bias == TrendBias.Bullish) confirmations++;
        if (p.Vwap?.Above == true) confirmations++;
        var confidence = confirmations >= 3 ? Confidence.High : confirmations >= 2 ? Confidence.Medium : Confidence.Low;
        return new SetupClassification(type, confidence, bias, evidence, breakout, keyLevel);
    }

    public static TrendBias Bias(IndicatorValues? m5, IndicatorValues? m15, StructureSnapshot? s15, VwapProjection? vwap)
    {
        var t = 0;
        t += m5?.Alignment switch { EmaAlignment.Bullish => 1, EmaAlignment.Bearish => -1, _ => 0 };
        t += m15?.Alignment switch { EmaAlignment.Bullish => 1, EmaAlignment.Bearish => -1, _ => 0 };
        t += s15?.Trend switch { StructureTrend.Uptrend => 1, StructureTrend.Downtrend => -1, _ => 0 };
        t += vwap is { } v ? (v.Above ? 1 : -1) : 0;
        return t >= 2 ? TrendBias.Bullish : t <= -2 ? TrendBias.Bearish : TrendBias.Neutral;
    }
}
