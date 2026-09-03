using TradingScanner.Analytics.Indicators;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;

namespace TradingScanner.Analytics;

/// <summary>
/// Per-symbol analytics across all timeframes. Fed with closed candles only (no look-ahead); the same object
/// serves live processing and backtest replay. Single-writer; publish results via <see cref="Snapshot"/>.
/// </summary>
public sealed class SymbolAnalytics
{
    private static readonly Timeframe[] Timeframes = TimeframeExtensions.All;
    private readonly TimeframeIndicators[] _tf;
    private readonly MarketStructureAnalyzer?[] _structure;
    private readonly SessionVwap _vwap = new();
    private readonly MomentumTracker _momentum = new();
    private DateTimeOffset _asOf;

    public Symbol Symbol { get; }

    public SymbolAnalytics(Symbol symbol, AnalyticsOptions options)
    {
        Symbol = symbol;
        _tf = Timeframes.Select(t => new TimeframeIndicators(t, options)).ToArray();
        _structure = Timeframes.Select(t => options.Structure.Timeframes.Contains(t) ? new MarketStructureAnalyzer(t, options.Structure) : null).ToArray();
    }

    public TimeframeIndicators Indicators(Timeframe tf) => _tf[Array.IndexOf(Timeframes, tf)];
    public MarketStructureAnalyzer? Structure(Timeframe tf) => _structure[Array.IndexOf(Timeframes, tf)];

    public void Update(in Candle c)
    {
        var i = Array.IndexOf(Timeframes, c.Timeframe);
        _tf[i].Update(c);
        _structure[i]?.Update(c, _tf[i].Last?.Atr);
        if (c.Timeframe == Timeframe.M1)
        {
            _vwap.Update(c);
            _momentum.Update((double)c.Close);
        }
        if (c.CloseTime > _asOf) _asOf = c.CloseTime;
    }

    /// <summary>
    /// Discard state and replay every timeframe's closed candles from the reader in chronological close order
    /// (after a history merge). <paramref name="onBar"/> receives the snapshot after each bar so downstream
    /// state machines can replay through exactly the live code path.
    /// </summary>
    public void Rebuild(ICandleHistoryReader reader, Action<AnalyticsSnapshot, Candle>? onBar = null)
    {
        foreach (var t in _tf) t.Reset();
        foreach (var st in _structure) st?.Reset();
        _vwap.Reset();
        _momentum.Reset();
        _asOf = default;

        var all = new List<Candle>();
        foreach (var tf in Timeframes)
        {
            var snap = reader.GetCandles(Symbol, tf);
            if (snap is not null) all.AddRange(snap.Closed);
        }
        // Lower timeframes first at equal close times so a 5m bar sees its own last 1m bar already applied.
        all.Sort(static (a, b) =>
        {
            var byClose = a.CloseTime.CompareTo(b.CloseTime);
            return byClose != 0 ? byClose : a.Timeframe.CompareTo(b.Timeframe);
        });
        foreach (var c in all)
        {
            Update(c);
            onBar?.Invoke(Snapshot(), c);
        }
    }

    public AnalyticsSnapshot Snapshot()
    {
        var values = new List<IndicatorValues>(_tf.Length);
        foreach (var t in _tf) if (t.Last is { } v) values.Add(v);

        VwapValues? vwap = _vwap.IsReady && _vwap.SessionStart is { } s ? new VwapValues(s, _vwap.Value, _vwap.Std, _vwap.Bars) : null;

        MomentumValues? momentum = null;
        if (_momentum.Count > 0)
        {
            var refs = new Dictionary<int, double>();
            var prev = new Dictionary<int, double>();
            foreach (var h in MomentumTracker.HorizonsMinutes)
            {
                if (_momentum.CloseAgo(h - 1) is { } r) refs[h] = r;
                if (_momentum.CloseAgo(2 * h - 1) is { } p) prev[h] = p;
            }
            momentum = new MomentumValues(_momentum.CloseAgo(0)!.Value, refs, prev);
        }

        var structure = new List<StructureSnapshot>(3);
        foreach (var st in _structure) if (st?.Last is { } ss) structure.Add(ss);

        return new AnalyticsSnapshot(Symbol, _asOf, values, vwap, momentum, structure);
    }
}
