using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;

namespace TradingScanner.Signals.Scanner;

/// <summary>
/// Anti-FOMO: measures how far price has already travelled relative to VWAP, EMA20, ATR, and support, and
/// whether volume looks climactic. Produces a 0..1 score and a hard "do not chase" verdict.
/// </summary>
public static class OverextensionAnalyzer
{
    public static OverextensionAssessment Assess(AnalyticsProjection p, OverextensionConfig cfg)
    {
        var m5 = p.For(Timeframe.M5);
        var s5 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M5);
        var atr = m5?.Atr;
        var price = p.Price;
        var flags = new List<string>();
        var parts = new List<double>();
        var hard = false;

        double? sigma = p.Vwap?.Sigma;
        if (sigma is { } sg)
        {
            parts.Add(Ramp(sg, cfg.VwapSigmaWarn - 1, cfg.VwapSigmaHard + 0.5));
            if (sg >= cfg.VwapSigmaHard) { flags.Add($"{sg:F1}σ above session VWAP"); hard = true; }
            else if (sg >= cfg.VwapSigmaWarn) flags.Add($"{sg:F1}σ above session VWAP");
        }

        double? emaDist = m5?.Ema20DistanceAtr(price);
        if (emaDist is { } ed)
        {
            parts.Add(Ramp(ed, cfg.Ema20AtrWarn - 1, cfg.Ema20AtrWarn + 2));
            if (ed >= cfg.Ema20AtrWarn) flags.Add($"{ed:F1} ATR above 5m EMA20");
        }

        double? move5 = null, move15 = null;
        if (atr is { } a && a > 0 && p.Momentum is { } mom)
        {
            if (mom.R5m is { } r5) { move5 = r5 * price / a; parts.Add(Ramp(move5.Value, cfg.Move5mAtrWarn - 0.5, cfg.Move5mAtrHard + 1)); if (move5 >= cfg.Move5mAtrHard) { flags.Add($"+{move5:F1} ATR in 5 minutes"); hard = true; } else if (move5 >= cfg.Move5mAtrWarn) flags.Add($"+{move5:F1} ATR in 5 minutes"); }
            if (mom.R15m is { } r15) { move15 = r15 * price / a; parts.Add(Ramp(move15.Value, cfg.Move15mAtrWarn - 1, cfg.Move15mAtrWarn + 3)); if (move15 >= cfg.Move15mAtrWarn) flags.Add($"+{move15:F1} ATR in 15 minutes"); }
        }
        double? r1h = p.Momentum?.R1h, r24h = p.Momentum?.R24h;
        if (r1h is { } h) { parts.Add(Ramp(h, cfg.Move1hPctWarn * 0.5, cfg.Move1hPctWarn * 2.5)); if (h >= cfg.Move1hPctWarn) flags.Add($"{h * 100:+0.0}% in the last hour"); }
        if (r24h is { } d) { parts.Add(Ramp(d, cfg.Move24hPctWarn * 0.4, cfg.Move24hPctWarn * 2.5)); if (d >= cfg.Move24hPctWarn) flags.Add($"{d * 100:+0.0}% in 24h"); }

        if (m5?.VolumeZ is { } z && m5.Rsi is { } rsi && z >= cfg.VolumeClimaxZ && rsi >= cfg.VolumeClimaxRsi)
        {
            flags.Add($"volume climax (z={z:F1}, RSI {rsi:F0})");
            parts.Add(1);
            hard = true;
        }

        if (p.Momentum is { Accel5m: > 0, Accel15m: > 0 } && move15 is >= 2.0)
        {
            flags.Add("parabolic: accelerating on both 5m and 15m");
            parts.Add(0.7);
        }

        double? supportDist = null;
        if (s5?.NearestSupport is { } sup && atr is { } at && at > 0)
        {
            supportDist = (price - sup.Price) / at;
            parts.Add(Ramp(supportDist.Value, cfg.SupportDistanceAtrWarn - 1, cfg.SupportDistanceAtrWarn + 3));
            if (supportDist >= cfg.SupportDistanceAtrWarn) flags.Add($"nearest support {supportDist:F1} ATR below");
        }

        var score = parts.Count == 0 ? 0 : 0.6 * parts.Max() + 0.4 * parts.Average();
        score = Math.Clamp(score, 0, 1);
        var doNotChase = hard || score >= cfg.DoNotChaseScore;
        return new OverextensionAssessment(score, doNotChase, flags, sigma, emaDist, move5, move15, r1h, r24h, supportDist);
    }

    /// <summary>0 at or below <paramref name="from"/>, 1 at or above <paramref name="to"/>.</summary>
    public static double Ramp(double x, double from, double to) => to <= from ? (x >= to ? 1 : 0) : Math.Clamp((x - from) / (to - from), 0, 1);
}
