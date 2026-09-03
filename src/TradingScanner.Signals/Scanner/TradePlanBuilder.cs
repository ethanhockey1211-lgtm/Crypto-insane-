using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;

namespace TradingScanner.Signals.Scanner;

/// <summary>
/// Turns a classified setup into entry zone, trigger, invalidation, stop, and three targets from structure and ATR.
/// Targets are capped by the next resistance levels so reward is never assumed through a wall.
/// </summary>
public static class TradePlanBuilder
{
    public static TradePlan? Build(SetupClassification setup, AnalyticsProjection p)
    {
        if (setup.Type == SetupType.None) return null;
        var m5 = p.For(Timeframe.M5);
        var s5 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M5);
        var s15 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M15);
        var price = p.Price;
        if (price <= 0) return null;
        var atr = m5?.Atr is { } a && a > 0 ? a : price * 0.005;
        var basis = new List<string>();

        double entryLow, entryHigh, invalidation, stop;
        string trigger;
        var b = setup.Breakout;

        switch (setup.Type)
        {
            case SetupType.Breakout:
            case SetupType.RangeBreakout:
            {
                var level = b!.Level.Price;
                entryLow = level;
                entryHigh = Math.Min(Math.Max(price, level + 0.3 * atr), level + 1.0 * atr);
                trigger = price <= level + 1.0 * atr ? $"5m close holding above {LevelBreakoutTracker.P(level)}" : $"wait for a retest of {LevelBreakoutTracker.P(level)}; price is {(price - level) / atr:F1} ATR beyond it";
                invalidation = level - 0.2 * atr;
                stop = level - 0.3 * atr;
                basis.Add($"stop {LevelBreakoutTracker.P(stop)} sits 0.3 ATR under the breakout level {LevelBreakoutTracker.P(level)}");
                break;
            }
            case SetupType.BreakoutRetest:
            {
                var level = b!.Level.Price;
                var retestLow = b.Retest is { } r ? level + r.ClosestApproachAtr * atr : level;
                entryLow = level;
                entryHigh = Math.Min(Math.Max(price, level + 0.3 * atr), level + 0.8 * atr);
                trigger = $"hold above {LevelBreakoutTracker.P(level)} after the retest";
                invalidation = Math.Min(level - 0.2 * atr, retestLow - 0.1 * atr);
                stop = invalidation - 0.05 * atr;
                basis.Add($"stop {LevelBreakoutTracker.P(stop)} is under the retest low {LevelBreakoutTracker.P(retestLow)} and the level {LevelBreakoutTracker.P(level)}");
                break;
            }
            case SetupType.VwapReclaim:
            {
                var vwap = p.Vwap!.Vwap;
                entryLow = Math.Min(price, vwap);
                entryHigh = Math.Max(price, vwap + 0.3 * atr);
                trigger = $"stay above VWAP {LevelBreakoutTracker.P(vwap)} on 1m closes";
                invalidation = vwap - 0.4 * atr;
                stop = vwap - 0.6 * atr;
                basis.Add($"stop {LevelBreakoutTracker.P(stop)} is 0.6 ATR under session VWAP");
                break;
            }
            case SetupType.SupportBounce:
            {
                var level = setup.KeyLevel ?? (s5?.NearestSupport?.Price ?? price - atr);
                entryLow = level;
                entryHigh = Math.Max(price, level + 0.3 * atr);
                trigger = $"hold above support {LevelBreakoutTracker.P(level)}";
                invalidation = level - 0.25 * atr;
                stop = level - 0.4 * atr;
                basis.Add($"stop {LevelBreakoutTracker.P(stop)} under support {LevelBreakoutTracker.P(level)}");
                break;
            }
            case SetupType.TrendPullback:
            case SetupType.MomentumContinuation:
            {
                var ema = m5?.Ema20 ?? price - atr;
                var swingLow = s5?.NearestSupport?.Price;
                entryLow = Math.Min(price, ema);
                entryHigh = price;
                trigger = setup.Type == SetupType.TrendPullback ? $"5m close back above EMA20 {LevelBreakoutTracker.P(ema)} with rising momentum" : "continuation on a 5m close above the prior bar high";
                var emaStop = ema - 0.75 * atr;
                stop = swingLow is { } sl && price - sl <= 2.5 * atr ? Math.Min(emaStop, sl - 0.15 * atr) : emaStop;
                invalidation = stop + 0.1 * atr;
                basis.Add(swingLow is { } s2 && price - s2 <= 2.5 * atr ? $"stop {LevelBreakoutTracker.P(stop)} under the last swing low {LevelBreakoutTracker.P(s2)} and EMA20" : $"stop {LevelBreakoutTracker.P(stop)} is 0.75 ATR under the 5m EMA20");
                break;
            }
            case SetupType.Reversal:
            {
                var low = setup.KeyLevel ?? (m5?.Low ?? price - atr);
                entryLow = Math.Min(price, low + 0.5 * atr);
                entryHigh = price;
                trigger = "5m close above the prior bar high confirming the divergence";
                invalidation = low - 0.15 * atr;
                stop = low - 0.3 * atr;
                basis.Add($"stop {LevelBreakoutTracker.P(stop)} under the divergence low {LevelBreakoutTracker.P(low)}");
                break;
            }
            default: // VolumeExpansion, VolatilityExpansion
            {
                var low = m5?.Low ?? price - atr;
                entryLow = Math.Min(price, low + 0.5 * atr);
                entryHigh = price;
                trigger = "hold above the expansion bar's midpoint";
                invalidation = low - 0.1 * atr;
                stop = low - 0.25 * atr;
                basis.Add($"stop {LevelBreakoutTracker.P(stop)} under the expansion bar low {LevelBreakoutTracker.P(low)}");
                break;
            }
        }

        var entry = (entryLow + entryHigh) / 2;
        var risk = entry - stop;
        if (risk <= 0 || double.IsNaN(risk)) return null;

        var resistances = new List<double>();
        foreach (var s in new[] { s5, s15 })
            if (s is not null) foreach (var l in s.Levels) if (l.Price > entry + 0.5 * risk) resistances.Add(l.Price);
        if (s5?.RangeHigh is { } rh && rh > entry + 0.5 * risk) resistances.Add(rh);
        resistances = resistances.Distinct().OrderBy(x => x).ToList();

        var t1 = Cap(entry + 2.0 * risk, resistances, entry + 1.0 * risk, basis, "T1");
        var t2 = Cap(Math.Max(t1 + 0.5 * risk, entry + 3.0 * risk), resistances.Where(r => r > t1 + 0.25 * risk).ToList(), t1 + 0.5 * risk, basis, "T2");
        var t3 = setup.Type == SetupType.RangeBreakout && s5 is { RangeHigh: { } h, RangeLow: { } lo } && h > lo
            ? Math.Max(t2 + 0.5 * risk, h + (h - lo))
            : Math.Max(t2 + 0.5 * risk, entry + 5.0 * risk);
        if (setup.Type == SetupType.RangeBreakout) basis.Add("T3 is the measured move: range height projected above the range high");

        return new TradePlan(entryLow, entryHigh, trigger, invalidation, stop, t1, t2, t3, risk,
            (t1 - entry) / risk, (t2 - entry) / risk, (t3 - entry) / risk, basis);
    }

    /// <summary>Use the first resistance at or beyond the minimum acceptable reward instead of blindly targeting through it.</summary>
    private static double Cap(double ideal, List<double> resistances, double minimum, List<string> basis, string name)
    {
        foreach (var r in resistances)
        {
            if (r < minimum) continue;
            if (r < ideal)
            {
                basis.Add($"{name} capped at resistance {LevelBreakoutTracker.P(r)}");
                return r;
            }
            break;
        }
        return ideal;
    }
}
