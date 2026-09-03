using TradingScanner.Analytics;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;

namespace TradingScanner.Signals.Breakouts;

/// <summary>Keeps one <see cref="LevelBreakoutTracker"/> per structure level for a symbol on the configured timeframe.</summary>
public sealed class SymbolBreakoutTracker
{
    private static readonly int[] Priority = BuildPriority();
    private readonly BreakoutOptions _o;
    private readonly Dictionary<string, LevelBreakoutTracker> _trackers = new(StringComparer.Ordinal);
    private BreakoutAnalysis? _last;

    public Symbol Symbol { get; }
    public BreakoutAnalysis? Last => _last;

    public SymbolBreakoutTracker(Symbol symbol, BreakoutOptions options)
    {
        Symbol = symbol;
        _o = options;
    }

    /// <summary>Advance every level tracker with the closed bar of the breakout timeframe.</summary>
    public BreakoutAnalysis Update(in Candle bar, StructureSnapshot structure, IndicatorValues indicators)
    {
        if (bar.Timeframe != _o.Timeframe) throw new ArgumentException($"Expected {_o.Timeframe} bar, got {bar.Timeframe}.");
        var close = (double)bar.Close;
        var atr = indicators.Atr ?? close * 0.005;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var statuses = new List<BreakoutStatus>(structure.Levels.Count);

        foreach (var level in structure.Levels)
        {
            seen.Add(level.Id);
            if (!_trackers.TryGetValue(level.Id, out var tracker))
            {
                // Direction from where price was at the start of this bar: this bar may itself be the breakout bar.
                tracker = new LevelBreakoutTracker(level, level.Price > (double)bar.Open ? BreakoutDirection.Up : BreakoutDirection.Down, _o, bar.CloseTime);
                _trackers[level.Id] = tracker;
            }
            statuses.Add(tracker.Update(bar, atr, indicators.RelVolume, level));
        }
        foreach (var id in _trackers.Keys.Where(k => !seen.Contains(k)).ToList()) _trackers.Remove(id);

        var maxDist = _o.MaxLevelDistanceAtr;
        BreakoutStatus? bestUp = null, bestDown = null;
        foreach (var s in statuses)
        {
            if (Math.Abs(s.Level.Price - close) / atr > maxDist && s.State is BreakoutState.Watching) continue;
            if (s.Direction == BreakoutDirection.Up) { if (Better(s, bestUp, close)) bestUp = s; }
            else if (Better(s, bestDown, close)) bestDown = s;
        }

        _last = new BreakoutAnalysis(Symbol, _o.Timeframe, bar.CloseTime, statuses, bestUp, bestDown);
        return _last;
    }

    private static bool Better(BreakoutStatus candidate, BreakoutStatus? current, double close)
    {
        if (current is null) return true;
        var pc = Priority[(int)candidate.State];
        var pk = Priority[(int)current.State];
        if (pc != pk) return pc > pk;
        return Math.Abs(candidate.Level.Price - close) < Math.Abs(current.Level.Price - close);
    }

    private static int[] BuildPriority()
    {
        var p = new int[8];
        p[(int)BreakoutState.RetestHeld] = 7;
        p[(int)BreakoutState.Confirmed] = 6;
        p[(int)BreakoutState.Retesting] = 5;
        p[(int)BreakoutState.Attempt] = 4;
        p[(int)BreakoutState.Approaching] = 3;
        p[(int)BreakoutState.Extended] = 2;
        p[(int)BreakoutState.Failed] = 1;
        p[(int)BreakoutState.Watching] = 0;
        return p;
    }

    public void Reset()
    {
        _trackers.Clear();
        _last = null;
    }
}
