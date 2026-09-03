using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;

namespace TradingScanner.Signals.Scanner;

/// <summary>Derives BTC/ETH state, breadth, and the risk regime from the universe's analytics projections.</summary>
public static class MarketContextBuilder
{
    public static readonly Symbol Btc = new("BTC-USD");
    public static readonly Symbol Eth = new("ETH-USD");

    public static MarketContext Build(IReadOnlyList<AnalyticsProjection> projections, DateTimeOffset now)
    {
        AssetState? btc = null, eth = null;
        var aboveVwap = 0; var positive1h = 0; var bullish = 0; var evaluated = 0;
        var relVols = new List<double>();
        foreach (var p in projections)
        {
            if (p.Symbol == Btc) btc = Asset(p);
            else if (p.Symbol == Eth) eth = Asset(p);
            var m5 = p.For(Timeframe.M5);
            if (m5 is null || p.Momentum is null) continue;
            evaluated++;
            if (p.Vwap?.Above == true) aboveVwap++;
            if (p.Momentum.R1h is > 0) positive1h++;
            if (m5.Alignment == EmaAlignment.Bullish) bullish++;
            if (m5.RelVolume is { } rv) relVols.Add(rv);
        }
        var n = Math.Max(1, evaluated);
        var breadthVwap = (double)aboveVwap / n;
        var breadth1h = (double)positive1h / n;
        var breadthAlign = (double)bullish / n;
        relVols.Sort();
        var medianRv = relVols.Count == 0 ? 0 : relVols[relVols.Count / 2];

        var notes = new List<string>();
        var score = 0;
        if (btc is not null)
        {
            score += btc.Trend switch { TrendBias.Bullish => 2, TrendBias.Bearish => -2, _ => 0 };
            if (btc.R1h is > 0.01) score += 1; else if (btc.R1h is < -0.01) score -= 1;
            if (btc.Dumping) { score -= 3; notes.Add($"BTC selling off: {Pct(btc.R5m)} in 5m, {Pct(btc.R15m)} in 15m"); }
            notes.Add($"BTC {btc.Trend.ToString().ToLowerInvariant()} trend, {(btc.AboveVwap == true ? "above" : "below")} session VWAP");
        }
        else notes.Add("BTC analytics unavailable; regime is breadth-only");
        if (breadthVwap >= 0.6) score += 1; else if (breadthVwap <= 0.4) score -= 1;
        if (breadth1h >= 0.6) score += 1; else if (breadth1h <= 0.4) score -= 1;
        notes.Add($"{breadthVwap:P0} of {evaluated} symbols above VWAP, {breadth1h:P0} up on the hour, median relative volume {medianRv:F2}×");

        var regime = score >= 4 ? MarketRegime.StrongRiskOn
            : score >= 2 ? MarketRegime.RiskOn
            : score <= -4 ? MarketRegime.StrongRiskOff
            : score <= -2 ? MarketRegime.RiskOff
            : MarketRegime.Neutral;
        var altsFavorable = regime is MarketRegime.RiskOn or MarketRegime.StrongRiskOn && btc?.Dumping != true && breadthVwap >= 0.5;
        notes.Add(altsFavorable ? "Altcoin conditions favor aggressive setups" : "Altcoin conditions do not favor aggressive setups; demand confirmation");

        return new MarketContext(now, regime, altsFavorable, btc, eth, breadthVwap, breadth1h, breadthAlign, medianRv, evaluated, notes);
    }

    public static AssetState Asset(AnalyticsProjection p)
    {
        var m5 = p.For(Timeframe.M5);
        var m15 = p.For(Timeframe.M15);
        var s15 = p.Structure.FirstOrDefault(s => s.Timeframe == Timeframe.M15);
        var mom = p.Momentum;
        var t = 0;
        t += Align(m5?.Alignment) + Align(m15?.Alignment);
        t += s15?.Trend switch { StructureTrend.Uptrend => 1, StructureTrend.Downtrend => -1, _ => 0 };
        t += p.Vwap is { } v ? (v.Above ? 1 : -1) : 0;
        var trend = t >= 2 ? TrendBias.Bullish : t <= -2 ? TrendBias.Bearish : TrendBias.Neutral;
        var r5 = mom?.R5m; var r15 = mom?.R15m;
        var dumping = r5 is < -0.008 || r15 is < -0.015 || (r5 is < -0.005 && m5?.RelVolume is >= 2.0);
        var summary = $"{p.Symbol.Value} {trend.ToString().ToLowerInvariant()}, {Pct(r5)} 5m / {Pct(mom?.R1h)} 1h, {(p.Vwap?.Above == true ? "above" : "below")} VWAP{(dumping ? ", DUMPING" : "")}";
        return new AssetState(p.Symbol.Value, p.Price, r5, r15, mom?.R1h, mom?.R24h, trend, p.Vwap?.Above, p.Vwap?.Sigma, m5?.AtrPct, m5?.RelVolume, dumping, summary);
    }

    private static int Align(EmaAlignment? a) => a switch { EmaAlignment.Bullish => 1, EmaAlignment.Bearish => -1, _ => 0 };
    public static string Pct(double? v) => v is { } x ? $"{x * 100:+0.00;-0.00}%" : "n/a";
}
