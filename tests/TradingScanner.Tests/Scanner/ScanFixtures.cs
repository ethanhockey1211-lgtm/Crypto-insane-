using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;

namespace TradingScanner.Tests.Scanner;

/// <summary>Hand-built analytics inputs so scanner rules can be tested one factor at a time.</summary>
public static class ScanFixtures
{
    public static readonly Symbol Xrp = new("XRP-USD");

    public static IndicatorValues Ind(Timeframe tf = Timeframe.M5, double close = 1.4, double? ema9 = null, double? ema20 = null, double? ema50 = null, double? ema200 = null,
        double? rsi = 60, double? atr = 0.01, double? relVol = 1.5, double? volumeZ = null, double? fastRatio = null, EmaAlignment align = EmaAlignment.Bullish,
        double? distEma20Atr = 0.5, double? atrRatio = null, double? buyShare = null, double? open = null, double? high = null, double? low = null, double? rangeRatio = null) =>
        new(tf, T.Base, close, ema9, ema20 ?? close - 0.5 * (atr ?? 0.01), ema50, ema200, rsi, null, atr, atr is { } a ? a / close : null, null, rangeRatio, relVol, volumeZ, fastRatio, null,
            align, null, null, CrossDirection.None, distEma20Atr, atrRatio, buyShare, open ?? close - 0.002, high ?? close + 0.003, low ?? close - 0.004);

    public static PriceLevel Level(string id, double price, int touches = 2) => new(id, price, LevelSource.Swing, touches, T.Base, T.Base, 0, touches);

    public static StructureSnapshot Struct(Timeframe tf = Timeframe.M5, double close = 1.4, StructureTrend trend = StructureTrend.Uptrend, double? rangeHigh = null, double? rangeLow = null, IReadOnlyList<Divergence>? divergences = null, long barIndex = 100, params PriceLevel[] levels) =>
        new(tf, T.Base, barIndex, close, 0.01, [], levels, trend, trend == StructureTrend.Uptrend ? "HH HL HH HL" : "", rangeHigh, rangeLow, 48, null, null, divergences);

    public static MomentumProjection Mom(double? r1m = 0.001, double? r5m = 0.006, double? r15m = 0.012, double? r1h = 0.02, double? r24h = 0.04, double? accel5 = 0.002, double? accel15 = 0.001) =>
        new(r1m, null, r5m, r15m, null, r1h, null, r24h, accel5, accel15, null);

    public static VwapProjection Vwap(double vwap = 1.39, double price = 1.4, double std = 0.01) =>
        new(T.Base, vwap, std, 300, (price - vwap) / vwap, std > 0 ? (price - vwap) / std : null, price > vwap);

    public static AnalyticsProjection Proj(double price = 1.4, IndicatorValues? m5 = null, IndicatorValues? m15 = null, StructureSnapshot? s5 = null, StructureSnapshot? s15 = null,
        MomentumProjection? mom = null, VwapProjection? vwap = null, VwapState? vwapState = null)
    {
        var tfs = new List<IndicatorValues>();
        tfs.Add(m5 ?? Ind(close: price));
        tfs.Add(m15 ?? Ind(Timeframe.M15, close: price));
        var st = new List<StructureSnapshot>();
        if (s5 is not null) st.Add(s5);
        if (s15 is not null) st.Add(s15);
        return new AnalyticsProjection(Xrp, T.Base, T.Base, price, tfs, vwap ?? Vwap(price: price), mom ?? Mom(), st, vwapState);
    }

    public static BreakoutStatus Status(PriceLevel level, BreakoutState state, BreakoutDirection dir = BreakoutDirection.Up, int barsSince = 1, double relVol = 2.1, double strength = 0.9, bool weak = false, RetestMetrics? retest = null, double distance = 0.8, int barsInState = 0, string narrative = "Closed above 1.4000 by 0.80 ATR on 2.1× volume") =>
        new(level, dir, state, T.Base, barsInState, barsSince, relVol, strength, weak, retest, distance, narrative);

    public static BreakoutAnalysis Breakouts(BreakoutStatus? bestUp, BreakoutStatus? bestDown = null, params BreakoutStatus[] others)
    {
        var all = new List<BreakoutStatus>();
        if (bestUp is not null) all.Add(bestUp);
        if (bestDown is not null) all.Add(bestDown);
        all.AddRange(others);
        return new BreakoutAnalysis(Xrp, Timeframe.M5, T.Base, all, bestUp, bestDown);
    }

    public static MarketContext Market(TrendBias btcTrend = TrendBias.Bullish, bool dumping = false, MarketRegime regime = MarketRegime.RiskOn, bool altsFavorable = true) =>
        new(T.Base, regime, altsFavorable,
            new AssetState("BTC-USD", 60000, 0.002, 0.004, 0.01, 0.02, btcTrend, true, 0.8, 0.004, 1.2, dumping, $"BTC-USD {btcTrend}"),
            null, 0.6, 0.6, 0.5, 1.1, 100, []);
}
