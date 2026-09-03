using TradingScanner.Analytics.Indicators;
using TradingScanner.Core.Market;

namespace TradingScanner.Analytics.Structure;

/// <summary>
/// Maintains swings, clustered horizontal levels, trend labels (HH/HL/LH/LL), a lookback range and the UTC session
/// range for one symbol/timeframe. Incremental per closed bar; single-writer.
/// Levels are stable objects: a new swing joins the nearest existing level within tolerance or starts a new one;
/// existing levels never merge, so a level's identity survives from bar to bar (breakout trackers key on it).
/// </summary>
public sealed class MarketStructureAnalyzer
{
    private sealed class LevelCluster
    {
        public required string Id;
        public required LevelSource Source;
        public double SumPrice;
        public int Touches;
        public DateTimeOffset First;
        public DateTimeOffset Last;
        public long LastBar;
        public double Price => SumPrice / Touches;
    }

    private readonly Timeframe _tf;
    private readonly StructureOptions _o;
    private readonly SwingDetector _detector;
    private readonly List<SwingPoint> _swings = new();
    private readonly List<LevelCluster> _levels = new();
    private readonly RollingWindow _highs;
    private readonly RollingWindow _lows;
    private long _barIndex = -1;
    private DateTimeOffset? _session;
    private double _sessionHigh = double.NaN, _sessionLow = double.NaN;
    private StructureSnapshot? _last;

    public MarketStructureAnalyzer(Timeframe tf, StructureOptions options)
    {
        _tf = tf;
        _o = options;
        _detector = new SwingDetector(options.SwingStrength);
        _highs = new RollingWindow(options.RangeLookback);
        _lows = new RollingWindow(options.RangeLookback);
    }

    public Timeframe Timeframe => _tf;
    public StructureSnapshot? Last => _last;
    public IReadOnlyList<SwingPoint> Swings => _swings;

    public void Update(in Candle c, double? atr)
    {
        if (c.Timeframe != _tf) throw new ArgumentException($"Expected {_tf}, got {c.Timeframe}.");
        _barIndex++;
        var close = (double)c.Close;
        var high = (double)c.High;
        var low = (double)c.Low;

        _highs.Add(high);
        _lows.Add(low);

        var session = SessionVwap.UtcDay(c.OpenTime);
        if (_session != session)
        {
            _session = session;
            _sessionHigh = high; _sessionLow = low;
        }
        else
        {
            if (high > _sessionHigh) _sessionHigh = high;
            if (low < _sessionLow) _sessionLow = low;
        }

        var tolerance = atr is { } a && a > 0 ? a * _o.LevelToleranceAtr : close * _o.LevelTolerancePct;
        var (sh, sl) = _detector.Update(c);
        if (sh is { } h) AddSwing(h, tolerance);
        if (sl is { } l) AddSwing(l, tolerance);

        // Expire stale levels.
        _levels.RemoveAll(lv => _barIndex - lv.LastBar > _o.LevelMaxAgeBars);

        var (trend, labels) = ClassifyTrend(tolerance);
        var levels = BuildLevels();
        double? rangeHigh = _highs.IsFull ? Max(_highs) : null;
        double? rangeLow = _lows.IsFull ? Min(_lows) : null;

        _last = new StructureSnapshot(_tf, c.CloseTime, _barIndex, close, atr, _swings.ToArray(), levels, trend, labels,
            rangeHigh, rangeLow, _o.RangeLookback, _sessionHigh, _sessionLow);
    }

    private void AddSwing(SwingPoint s, double tolerance)
    {
        _swings.Add(s);
        if (_swings.Count > _o.MaxSwings) _swings.RemoveAt(0);

        LevelCluster? nearest = null;
        var bestDist = double.MaxValue;
        foreach (var lv in _levels)
        {
            var d = Math.Abs(lv.Price - s.Price);
            if (d <= tolerance && d < bestDist) { bestDist = d; nearest = lv; }
        }
        if (nearest is null)
        {
            nearest = new LevelCluster { Id = $"{_tf.Label()}:{s.BarTime.ToUnixTimeSeconds()}:{(s.Type == SwingType.High ? 'H' : 'L')}", Source = LevelSource.Swing, First = s.BarTime };
            _levels.Add(nearest);
        }
        nearest.SumPrice += s.Price;
        nearest.Touches++;
        nearest.Last = s.BarTime;
        nearest.LastBar = s.BarIndex;
    }

    private IReadOnlyList<PriceLevel> BuildLevels()
    {
        var list = new List<PriceLevel>(_levels.Count);
        foreach (var lv in _levels)
        {
            var age = Math.Max(0, _barIndex - lv.LastBar);
            var recency = Math.Exp(-age / (double)Math.Max(1, _o.LevelMaxAgeBars / 3));
            var strength = lv.Touches * (0.5 + 0.5 * recency);
            list.Add(new PriceLevel(lv.Id, lv.Price, lv.Source, lv.Touches, lv.First, lv.Last, lv.LastBar, strength));
        }
        return list.OrderByDescending(l => l.Strength).ThenByDescending(l => l.LastTouch).Take(_o.MaxLevels).ToList();
    }

    /// <summary>
    /// Label each swing against the previous swing of the same type: HH/LH/EH for highs, HL/LL/EL for lows, where
    /// "equal" means within the level tolerance. Uptrend = last two highs higher and last two lows higher;
    /// downtrend mirrored; anything else (including equal highs/lows) is a range.
    /// </summary>
    private (StructureTrend, string) ClassifyTrend(double tolerance)
    {
        var labels = new List<string>();
        SwingPoint? lastHigh = null, lastLow = null;
        var highs = new List<int>(); // +1 higher, 0 equal, -1 lower
        var lows = new List<int>();
        foreach (var s in _swings)
        {
            if (s.Type == SwingType.High)
            {
                if (lastHigh is { } lh) { var c = Compare(s.Price, lh.Price, tolerance); highs.Add(c); labels.Add(c > 0 ? "HH" : c < 0 ? "LH" : "EH"); }
                lastHigh = s;
            }
            else
            {
                if (lastLow is { } ll) { var c = Compare(s.Price, ll.Price, tolerance); lows.Add(c); labels.Add(c > 0 ? "HL" : c < 0 ? "LL" : "EL"); }
                lastLow = s;
            }
        }
        var text = string.Join(' ', labels.TakeLast(6));
        if (highs.Count < 2 || lows.Count < 2) return (StructureTrend.Unknown, text);
        var rh = highs.TakeLast(2).ToArray();
        var rl = lows.TakeLast(2).ToArray();
        if (rh.All(x => x > 0) && rl.All(x => x > 0)) return (StructureTrend.Uptrend, text);
        if (rh.All(x => x < 0) && rl.All(x => x < 0)) return (StructureTrend.Downtrend, text);
        return (StructureTrend.Range, text);
    }

    private static int Compare(double a, double b, double tolerance) => a > b + tolerance ? 1 : a < b - tolerance ? -1 : 0;

    private static double Max(RollingWindow w) { var m = double.MinValue; for (var i = 0; i < w.Count; i++) m = Math.Max(m, w.FromEnd(i)); return m; }
    private static double Min(RollingWindow w) { var m = double.MaxValue; for (var i = 0; i < w.Count; i++) m = Math.Min(m, w.FromEnd(i)); return m; }

    public void Reset()
    {
        _detector.Reset(); _swings.Clear(); _levels.Clear(); _highs.Clear(); _lows.Clear();
        _barIndex = -1; _session = null; _sessionHigh = double.NaN; _sessionLow = double.NaN; _last = null;
    }
}
