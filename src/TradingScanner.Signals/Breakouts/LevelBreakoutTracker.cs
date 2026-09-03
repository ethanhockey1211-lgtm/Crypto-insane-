using System.Globalization;
using TradingScanner.Analytics.Structure;
using TradingScanner.Core.Market;

namespace TradingScanner.Signals.Breakouts;

/// <summary>
/// State machine for one horizontal level in one direction. All thresholds are in ATR units so the same
/// logic applies to a $0.20 coin and a $60,000 coin. Consumes closed bars only.
/// </summary>
public sealed class LevelBreakoutTracker
{
    private readonly BreakoutOptions _o;
    private PriceLevel _level;
    private BreakoutDirection _dir;
    private BreakoutState _state = BreakoutState.Watching;
    private DateTimeOffset _stateSince;
    private int _barsInState;
    private int? _barsSinceBreakout;
    private double? _breakoutRelVol, _breakoutStrength;
    private bool _weakVolume;
    // retest bookkeeping
    private double _retestClosest, _retestRelVolSum;
    private int _retestBars;
    private double? _recoveryRelVol;
    private int? _recoveryBars;
    private bool _retestHeld;
    private double _lastDistance;
    private double? _prevClose;
    private string _narrative = "";

    public LevelBreakoutTracker(PriceLevel level, BreakoutDirection direction, BreakoutOptions options, DateTimeOffset now)
    {
        _level = level;
        _dir = direction;
        _o = options;
        _stateSince = now;
    }

    public PriceLevel Level => _level;
    public BreakoutDirection Direction => _dir;
    public BreakoutState State => _state;

    public BreakoutStatus Status => new(
        _level, _dir, _state, _stateSince, _barsInState, _barsSinceBreakout, _breakoutRelVol, _breakoutStrength, _weakVolume,
        _state is BreakoutState.Retesting or BreakoutState.RetestHeld || (_retestBars > 0 && _state is BreakoutState.Confirmed or BreakoutState.Extended or BreakoutState.Failed)
            ? new RetestMetrics(_retestClosest, Math.Max(0, -_retestClosest), _retestBars, _retestBars > 0 ? _retestRelVolSum / _retestBars : 0, _recoveryRelVol, _recoveryBars, _retestHeld)
            : null,
        _lastDistance, _narrative);

    public BreakoutStatus Update(in Candle c, double atr, double? relVol, PriceLevel currentLevel)
    {
        _level = currentLevel;
        if (atr <= 0) atr = Math.Max((double)c.Close * 0.001, 1e-9);
        var close = (double)c.Close; var high = (double)c.High; var low = (double)c.Low; var open = (double)c.Open;
        var level = _level.Price;

        if (_state == BreakoutState.Watching)
        {
            // Price on the wrong side of a watched level: watch the crossing in the other direction instead.
            // The reference is where price WAS (previous close, or this bar's open), never this bar's close,
            // because this bar may be the breakout bar itself.
            var reference = _prevClose ?? open;
            var wanted = reference > level ? BreakoutDirection.Down : BreakoutDirection.Up;
            if (wanted != _dir) _dir = wanted;
        }
        _prevClose = close;

        var d = _dir == BreakoutDirection.Up ? 1.0 : -1.0;
        var beyondClose = d * (close - level) / atr;
        var beyondExtreme = _dir == BreakoutDirection.Up ? (high - level) / atr : (level - low) / atr;
        var pullback = _dir == BreakoutDirection.Up ? (low - level) / atr : (level - high) / atr;
        var range = high - low;
        var closeStrength = range > 0 ? (_dir == BreakoutDirection.Up ? (close - low) / range : (high - close) / range) : 1.0;
        var bullishBar = _dir == BreakoutDirection.Up ? close > open : close < open;
        var rv = relVol ?? double.NaN;
        var volumeOk = !double.IsNaN(rv) && rv >= _o.RelVolConfirm;
        _lastDistance = beyondClose;
        _barsInState++;
        if (_barsSinceBreakout is { } b) _barsSinceBreakout = b + 1;

        switch (_state)
        {
            case BreakoutState.Watching:
            case BreakoutState.Approaching:
                if (beyondClose > _o.ConfirmBufferAtr)
                {
                    if (volumeOk && closeStrength >= _o.MinCloseStrength) Confirm(c, rv, closeStrength, weak: false, beyondClose);
                    else Transition(BreakoutState.Attempt, c, $"Closed {Beyond()} {P(level)} by {beyondClose:F2} ATR but {(volumeOk ? $"close was weak ({closeStrength:P0} of range)" : $"volume only {Rv(rv)}")} — unconfirmed");
                }
                else if (beyondExtreme > 0)
                {
                    Transition(BreakoutState.Attempt, c, $"Wicked {Beyond()} {P(level)} ({beyondExtreme:F2} ATR) but closed back {Inside()} on {Rv(rv)} — unconfirmed");
                }
                else if (-beyondClose <= _o.ApproachAtr)
                {
                    if (_state != BreakoutState.Approaching) Transition(BreakoutState.Approaching, c, $"{-beyondClose:F2} ATR {Inside()} {P(level)}");
                    else _narrative = $"{-beyondClose:F2} ATR {Inside()} {P(level)} for {_barsInState} bars";
                }
                else if (_state != BreakoutState.Watching)
                {
                    Transition(BreakoutState.Watching, c, $"{-beyondClose:F2} ATR from {P(level)}");
                }
                else
                {
                    _narrative = $"{-beyondClose:F2} ATR from {P(level)}";
                }
                break;

            case BreakoutState.Attempt:
                if (beyondClose < -_o.FailBufferAtr) Fail(c, beyondClose, "after an unconfirmed attempt");
                else if (beyondClose > _o.ConfirmBufferAtr && volumeOk && closeStrength >= _o.MinCloseStrength) Confirm(c, rv, closeStrength, weak: false, beyondClose);
                else if (_barsInState + 1 >= _o.MaxBarsInAttempt) // bars including the attempt bar itself
                {
                    if (beyondClose > _o.ConfirmBufferAtr) Confirm(c, rv, closeStrength, weak: true, beyondClose);
                    else Transition(BreakoutState.Watching, c, $"Attempt fizzled: {_barsInState + 1} bars without holding {Beyond()} {P(level)}");
                }
                else _narrative = $"Attempt in progress ({_barsInState + 1} bars), close {beyondClose:+0.00;-0.00} ATR vs {P(level)}, volume {Rv(rv)}";
                break;

            case BreakoutState.Confirmed:
            case BreakoutState.RetestHeld:
                if (beyondClose < -_o.FailBufferAtr) LoseLevel(c, beyondClose);
                else if (beyondClose > _o.ExtendedAtr) Transition(BreakoutState.Extended, c, $"{beyondClose:F1} ATR {Beyond()} {P(level)} — do not chase");
                else if (pullback <= _o.RetestZoneAtr && _state == BreakoutState.Confirmed)
                {
                    _retestClosest = pullback; _retestRelVolSum = double.IsNaN(rv) ? 0 : rv; _retestBars = 1; _recoveryRelVol = null; _recoveryBars = null; _retestHeld = false;
                    Transition(BreakoutState.Retesting, c, $"Pulled back to {P(level)} (closest {pullback:+0.00;-0.00} ATR) on {Rv(rv)}");
                }
                else if (pullback <= _o.RetestZoneAtr && _state == BreakoutState.RetestHeld)
                {
                    // A second retest after one already held: treat like a fresh retest of a proven level.
                    _retestClosest = pullback; _retestRelVolSum = double.IsNaN(rv) ? 0 : rv; _retestBars = 1; _recoveryRelVol = null; _recoveryBars = null; _retestHeld = false;
                    Transition(BreakoutState.Retesting, c, $"Second retest of {P(level)} (closest {pullback:+0.00;-0.00} ATR)");
                }
                else _narrative = $"Holding {beyondClose:F2} ATR {Beyond()} {P(level)}, {_barsSinceBreakout} bars since break";
                break;

            case BreakoutState.Retesting:
                _retestBars++;
                _retestRelVolSum += double.IsNaN(rv) ? 0 : rv;
                if (pullback < _retestClosest) _retestClosest = pullback;
                if (beyondClose < -_o.FailBufferAtr) Fail(c, beyondClose, $"during the retest ({_retestBars} bars)");
                else if (beyondClose > _o.ConfirmBufferAtr && bullishBar)
                {
                    _recoveryRelVol = double.IsNaN(rv) ? null : rv; _recoveryBars = _retestBars; _retestHeld = true;
                    Transition(BreakoutState.RetestHeld, c, $"Retest held: reclaimed {P(level)} after {_retestBars} bars (closest {_retestClosest:+0.00;-0.00} ATR), recovery volume {Rv(rv)}");
                }
                else if (_retestBars >= _o.MaxRetestBars)
                {
                    if (beyondClose >= 0) Transition(BreakoutState.Confirmed, c, $"Retest stalled {_retestBars} bars but price stayed {Beyond()} {P(level)}");
                    else Fail(c, beyondClose, $"retest lingered {_retestBars} bars without reclaiming");
                }
                else _narrative = $"Retesting {P(level)}: {_retestBars} bars, closest {_retestClosest:+0.00;-0.00} ATR, avg volume {Rv(_retestRelVolSum / _retestBars)}";
                break;

            case BreakoutState.Extended:
                if (beyondClose < -_o.FailBufferAtr) LoseLevel(c, beyondClose);
                else if (beyondClose <= _o.ExtendedAtr) Transition(BreakoutState.Confirmed, c, $"Cooled to {beyondClose:F2} ATR {Beyond()} {P(level)}");
                else _narrative = $"{beyondClose:F1} ATR {Beyond()} {P(level)} — do not chase";
                break;

            case BreakoutState.Failed:
                if (_barsInState >= _o.FailedCooldownBars)
                {
                    ResetBreakout();
                    Transition(BreakoutState.Watching, c, $"Watching {P(level)} again after failed break");
                }
                else _narrative = $"Failed break of {P(level)} {_barsInState} bars ago";
                break;
        }

        return Status;
    }

    private void Confirm(in Candle c, double rv, double strength, bool weak, double beyond)
    {
        _barsSinceBreakout = 0;
        _breakoutRelVol = double.IsNaN(rv) ? null : rv;
        _breakoutStrength = strength;
        _weakVolume = weak;
        _retestBars = 0; _retestHeld = false; _recoveryRelVol = null; _recoveryBars = null;
        Transition(BreakoutState.Confirmed, c, weak
            ? $"Held {Beyond()} {P(level: _level.Price)} for {_o.MaxBarsInAttempt} bars on modest volume ({Rv(rv)}) — confirmed by time, not volume"
            : $"Closed {Beyond()} {P(_level.Price)} by {beyond:F2} ATR on {Rv(rv)}, close in top {strength:P0} of range");
    }

    private void Fail(in Candle c, double beyond, string context)
    {
        Transition(BreakoutState.Failed, c, $"Failed: closed back {Inside()} {P(_level.Price)} by {-beyond:F2} ATR {context}");
    }

    private void LoseLevel(in Candle c, double beyond)
    {
        if (_barsSinceBreakout is { } b && b <= _o.FailWindowBars) Fail(c, beyond, $"{b} bars after the break");
        else
        {
            ResetBreakout();
            Transition(BreakoutState.Watching, c, $"Level {P(_level.Price)} lost {_barsSinceBreakout} bars after the break");
        }
    }

    private void ResetBreakout()
    {
        _barsSinceBreakout = null; _breakoutRelVol = null; _breakoutStrength = null; _weakVolume = false;
        _retestBars = 0; _retestHeld = false; _recoveryRelVol = null; _recoveryBars = null; _retestClosest = 0; _retestRelVolSum = 0;
    }

    private void Transition(BreakoutState state, in Candle c, string narrative)
    {
        _state = state;
        _stateSince = c.CloseTime;
        _barsInState = 0;
        _narrative = narrative;
    }

    private string Beyond() => _dir == BreakoutDirection.Up ? "above" : "below";
    private string Inside() => _dir == BreakoutDirection.Up ? "below" : "above";
    private static string Rv(double rv) => double.IsNaN(rv) ? "unknown volume" : $"{rv:F1}× volume";

    public static string P(double level)
    {
        var digits = level >= 1000 ? 1 : level >= 100 ? 2 : level >= 1 ? 4 : level >= 0.01 ? 5 : 7;
        return level.ToString("F" + digits, CultureInfo.InvariantCulture);
    }
}
