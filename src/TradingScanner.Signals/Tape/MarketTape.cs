using TradingScanner.Core.Market;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Signals.Tape;

public enum TapeEventKind : byte
{
    BreakoutConfirmed, RetestHeld, BreakoutFailed, Extended, SetupAppeared, VwapReclaimed, VwapLost, VolumeSurge, ScoreCrossed, NewSessionHigh, BtcDump, RegimeChange,
}

public enum TapeSeverity : byte { Info = 0, Notice = 1, Alert = 2 }

public sealed record TapeEvent(long Id, DateTimeOffset At, string? Symbol, TapeEventKind Kind, TapeSeverity Severity, string Text);

/// <summary>
/// "What's moving now": derived purely by diffing consecutive scanner snapshots, so every line on the tape is a
/// state transition the engine actually observed. Ring buffer of the most recent events.
/// </summary>
public sealed class MarketTape
{
    private readonly object _gate = new();
    private readonly TapeEvent[] _ring;
    private int _head, _count;
    private long _nextId;
    private readonly Dictionary<Symbol, DateTimeOffset> _volumeSurgeBar = new();

    public MarketTape(int capacity = 300)
    {
        _ring = new TapeEvent[capacity];
    }

    public event Action<TapeEvent>? Published;

    public IReadOnlyList<TapeEvent> Recent(int n = 100)
    {
        lock (_gate)
        {
            n = Math.Min(n, _count);
            var list = new List<TapeEvent>(n);
            for (var i = 0; i < n; i++) list.Add(_ring[(_head + _count - 1 - i + _ring.Length) % _ring.Length]);
            return list;
        }
    }

    public List<TapeEvent> Observe(ScannerSnapshot? previous, ScannerSnapshot current, double setupThreshold)
    {
        var events = new List<TapeEvent>();
        if (previous is not null && previous.Market.Regime != current.Market.Regime)
            events.Add(Make(current.At, null, TapeEventKind.RegimeChange, TapeSeverity.Alert, $"Market regime {previous.Market.Regime} → {current.Market.Regime}"));
        if (current.Market.Btc is { Dumping: true } && previous?.Market.Btc?.Dumping != true)
            events.Add(Make(current.At, "BTC-USD", TapeEventKind.BtcDump, TapeSeverity.Alert, $"BTC selling off: {MarketContextBuilder.Pct(current.Market.Btc.R5m)} in 5m — altcoin scores reduced"));

        foreach (var o in current.Opportunities)
        {
            var prev = previous?.Get(o.Symbol);
            var sym = o.Symbol.Value;
            var up = o.Setup.Breakout is { Direction: BreakoutDirection.Up } b ? b : null;
            var prevUp = prev?.Setup.Breakout is { Direction: BreakoutDirection.Up } pb ? pb : null;
            if (up is not null && (prevUp is null || prevUp.Level.Id != up.Level.Id || prevUp.State != up.State))
            {
                switch (up.State)
                {
                    case BreakoutState.Confirmed when !up.WeakVolume:
                        events.Add(Make(o.At, sym, TapeEventKind.BreakoutConfirmed, TapeSeverity.Notice, $"{sym} broke {LevelBreakoutTracker.P(up.Level.Price)}: {up.Narrative}")); break;
                    case BreakoutState.RetestHeld:
                        events.Add(Make(o.At, sym, TapeEventKind.RetestHeld, TapeSeverity.Notice, $"{sym} retest of {LevelBreakoutTracker.P(up.Level.Price)} held")); break;
                    case BreakoutState.Failed:
                        events.Add(Make(o.At, sym, TapeEventKind.BreakoutFailed, TapeSeverity.Notice, $"{sym} failed breakout of {LevelBreakoutTracker.P(up.Level.Price)}")); break;
                    case BreakoutState.Extended:
                        events.Add(Make(o.At, sym, TapeEventKind.Extended, TapeSeverity.Info, $"{sym} {up.DistanceAtr:F1} ATR beyond {LevelBreakoutTracker.P(up.Level.Price)} — do not chase")); break;
                }
            }
            if (o.Setup.Type != SetupType.None && o.Score >= setupThreshold && (prev is null || prev.Setup.Type != o.Setup.Type || prev.Score < setupThreshold))
                events.Add(Make(o.At, sym, TapeEventKind.SetupAppeared, TapeSeverity.Notice, $"{sym} {Label(o.Setup.Type)} setup, score {o.Score:F0} ({o.Setup.Confidence} confidence)"));
            if (prev is not null && o.Score >= 80 && prev.Score < 80)
                events.Add(Make(o.At, sym, TapeEventKind.ScoreCrossed, TapeSeverity.Info, $"{sym} score crossed 80 ({prev.Score:F0} → {o.Score:F0})"));
            if (o.Metrics.RelVol5m is >= 3.0 && (prev?.Metrics.RelVol5m is null or < 3.0))
            {
                lock (_gate)
                {
                    if (!_volumeSurgeBar.TryGetValue(o.Symbol, out var last) || last != o.At)
                    {
                        _volumeSurgeBar[o.Symbol] = o.At;
                        events.Add(Make(o.At, sym, TapeEventKind.VolumeSurge, TapeSeverity.Notice, $"{sym} volume {o.Metrics.RelVol5m:F1}× its 5m baseline"));
                    }
                }
            }
            if (prev is not null && o.Metrics.VwapDeviationPct is { } dev && prev.Metrics.VwapDeviationPct is { } pdev)
            {
                if (dev > 0 && pdev <= 0) events.Add(Make(o.At, sym, TapeEventKind.VwapReclaimed, TapeSeverity.Info, $"{sym} reclaimed session VWAP {(o.Metrics.Vwap is { } v ? LevelBreakoutTracker.P(v) : "")}"));
                else if (dev < 0 && pdev >= 0) events.Add(Make(o.At, sym, TapeEventKind.VwapLost, TapeSeverity.Info, $"{sym} lost session VWAP"));
            }
        }

        if (events.Count > 0)
        {
            lock (_gate)
            {
                foreach (var e in events)
                {
                    if (_count == _ring.Length) { _ring[_head] = e; _head = (_head + 1) % _ring.Length; }
                    else _ring[(_head + _count++) % _ring.Length] = e;
                }
            }
            foreach (var e in events) Published?.Invoke(e);
        }
        return events;
    }

    private TapeEvent Make(DateTimeOffset at, string? symbol, TapeEventKind kind, TapeSeverity severity, string text) =>
        new(Interlocked.Increment(ref _nextId), at, symbol, kind, severity, text);

    public static string Label(SetupType t) => t switch
    {
        SetupType.Breakout => "Breakout",
        SetupType.BreakoutRetest => "Breakout + Retest",
        SetupType.VwapReclaim => "VWAP Reclaim",
        SetupType.SupportBounce => "Support Bounce",
        SetupType.MomentumContinuation => "Momentum Continuation",
        SetupType.RangeBreakout => "Range Breakout",
        SetupType.TrendPullback => "Trend Pullback",
        SetupType.Reversal => "Reversal",
        SetupType.VolumeExpansion => "Volume Expansion",
        SetupType.VolatilityExpansion => "Volatility Expansion",
        _ => "None",
    };
}
