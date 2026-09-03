using TradingScanner.Signals.Scanner;

namespace TradingScanner.Signals.Alerts;

/// <summary>Fields an alert condition can reference. Numeric, text, or boolean; see <see cref="AlertValues"/>.</summary>
public enum AlertField
{
    Price, Score, Rank, RelVol, R1m, R5m, R15m, R1h, R24h, Rsi5m, VwapDeviationPct, AboveVwap,
    BreakoutState, Setup, Confidence, DoNotChase, SpreadBps, KeyLevelDistancePct,
    BtcTrend, BtcDumping, Regime,
}

public enum AlertOperator { Gt, Gte, Lt, Lte, Eq, Ne, CrossesAbove, CrossesBelow }

public sealed record AlertCondition(AlertField Field, AlertOperator Operator, string Value);

public sealed record AlertRule(
    Guid Id,
    string Name,
    bool Enabled,
    /// <summary>Null = evaluate every symbol in the universe independently.</summary>
    string? Symbol,
    IReadOnlyList<AlertCondition> Conditions,
    /// <summary>All conditions must hold continuously for this long before the alert fires. 0 = immediately.</summary>
    int HoldSeconds,
    /// <summary>Minimum time between firings for the same rule and symbol.</summary>
    int CooldownSeconds,
    IReadOnlyList<string> Channels,
    string? WebhookUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastFiredAt,
    /// <summary>False (default): fire once when the conditions become true; they must turn false and true again to re-fire.
    /// True: keep re-firing every cooldown while the conditions stay true (reminder style).</summary>
    bool RepeatWhileTrue = false);

public sealed record AlertEvent(
    Guid Id,
    Guid RuleId,
    string RuleName,
    DateTimeOffset At,
    string Symbol,
    string Message,
    IReadOnlyDictionary<string, string> Values);

/// <summary>Typed value bag extracted from an opportunity and the market context.</summary>
public readonly record struct AlertValue(double? Number, string? Text, bool? Flag)
{
    public static AlertValue Of(double? n) => new(n, null, null);
    public static AlertValue Of(string? t) => new(null, t, null);
    public static AlertValue Of(bool b) => new(null, null, b);
    public bool IsMissing => Number is null && Text is null && Flag is null;
    public override string ToString() => Number is { } n ? n.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) : Text ?? (Flag is { } f ? (f ? "true" : "false") : "");
}

public static class AlertValues
{
    public static IReadOnlyDictionary<AlertField, AlertValue> From(Opportunity o, MarketContext market)
    {
        var m = o.Metrics;
        var dist = o.Setup.KeyLevel is { } k && o.Price > 0 ? (k - o.Price) / o.Price : (double?)null;
        return new Dictionary<AlertField, AlertValue>
        {
            [AlertField.Price] = AlertValue.Of(o.Price),
            [AlertField.Score] = AlertValue.Of(o.Score),
            [AlertField.Rank] = AlertValue.Of(o.Rank),
            [AlertField.RelVol] = AlertValue.Of(m.RelVol5m),
            [AlertField.R1m] = AlertValue.Of(m.R1m),
            [AlertField.R5m] = AlertValue.Of(m.R5m),
            [AlertField.R15m] = AlertValue.Of(m.R15m),
            [AlertField.R1h] = AlertValue.Of(m.R1h),
            [AlertField.R24h] = AlertValue.Of(m.R24h),
            [AlertField.Rsi5m] = AlertValue.Of(m.Rsi5m),
            [AlertField.VwapDeviationPct] = AlertValue.Of(m.VwapDeviationPct),
            [AlertField.AboveVwap] = m.VwapDeviationPct is { } d ? AlertValue.Of(d > 0) : default,
            [AlertField.BreakoutState] = AlertValue.Of(o.Setup.Breakout?.State.ToString()),
            [AlertField.Setup] = AlertValue.Of(o.Setup.Type.ToString()),
            [AlertField.Confidence] = AlertValue.Of(o.Setup.Confidence.ToString()),
            [AlertField.DoNotChase] = AlertValue.Of(o.Overextension.DoNotChase),
            [AlertField.SpreadBps] = AlertValue.Of(m.SpreadBps),
            [AlertField.KeyLevelDistancePct] = AlertValue.Of(dist),
            [AlertField.BtcTrend] = AlertValue.Of(market.Btc?.Trend.ToString()),
            [AlertField.BtcDumping] = market.Btc is { } b ? AlertValue.Of(b.Dumping) : default,
            [AlertField.Regime] = AlertValue.Of(market.Regime.ToString()),
        };
    }

    public static bool IsNumeric(AlertField f) => f is AlertField.Price or AlertField.Score or AlertField.Rank or AlertField.RelVol or AlertField.R1m or AlertField.R5m or AlertField.R15m or AlertField.R1h or AlertField.R24h or AlertField.Rsi5m or AlertField.VwapDeviationPct or AlertField.SpreadBps or AlertField.KeyLevelDistancePct;
    public static bool IsBoolean(AlertField f) => f is AlertField.AboveVwap or AlertField.DoNotChase or AlertField.BtcDumping;
}
