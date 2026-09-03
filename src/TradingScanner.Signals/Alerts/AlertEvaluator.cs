using System.Globalization;

namespace TradingScanner.Signals.Alerts;

/// <summary>Per rule-and-symbol evaluation state: when the conditions first held, and when we last fired.</summary>
public sealed class AlertState
{
    public DateTimeOffset? HeldSince { get; set; }
    public DateTimeOffset? LastFired { get; set; }
    public IReadOnlyDictionary<AlertField, AlertValue>? Previous { get; set; }
    /// <summary>Whether every condition held at the previous evaluation (edge detection).</summary>
    public bool WasSatisfied { get; set; }
}

public sealed record AlertDecision(bool Fire, bool Holding, TimeSpan? HeldFor, string? Reason);

/// <summary>
/// Pure evaluation of one rule for one symbol. All conditions must hold; cross operators require the previous
/// evaluation to have been on the other side. Rules are edge-triggered: the conjunction must go from false to true
/// to arm (or, with RepeatWhileTrue, every cooldown). A hold time keeps single-tick wicks from firing anything.
/// </summary>
public static class AlertEvaluator
{
    public static AlertDecision Evaluate(AlertRule rule, IReadOnlyDictionary<AlertField, AlertValue> current, AlertState state, DateTimeOffset now)
    {
        var previous = state.Previous;
        var wasSatisfied = state.WasSatisfied;
        state.Previous = current;

        var satisfiedNow = true;      // every condition in its "state" form (for crosses: on the target side)
        var crossedNow = true;        // every cross condition had its previous value on the other side
        string? failing = null;
        foreach (var c in rule.Conditions)
        {
            if (!current.TryGetValue(c.Field, out var cur) || cur.IsMissing) { satisfiedNow = false; failing = $"{c.Field} unavailable"; break; }
            if (!Holds(c, cur)) { satisfiedNow = false; failing = Describe(c, cur); break; }
            if (c.Operator is AlertOperator.CrossesAbove or AlertOperator.CrossesBelow)
            {
                var wasOtherSide = previous is not null && previous.TryGetValue(c.Field, out var prev) && !prev.IsMissing && !Holds(c, prev);
                if (!wasOtherSide) crossedNow = false; // no previous value, or already on the target side: no cross this tick
            }
        }
        state.WasSatisfied = satisfiedNow;

        if (!satisfiedNow)
        {
            state.HeldSince = null;
            return new AlertDecision(false, false, null, failing);
        }

        // Arm the hold window on the false→true edge (or every cooldown when repeating). Cross conditions arm only
        // at the crossing tick; afterwards they merely keep an armed window alive.
        if (state.HeldSince is null)
        {
            var edge = !wasSatisfied || rule.RepeatWhileTrue;
            if (!edge) return new AlertDecision(false, false, null, "already fired; waiting for conditions to reset");
            if (!crossedNow) return new AlertDecision(false, false, null, "waiting for a cross");
            state.HeldSince = now;
        }
        var held = now - state.HeldSince.Value;
        if (held < TimeSpan.FromSeconds(rule.HoldSeconds)) return new AlertDecision(false, true, held, $"holding {held.TotalSeconds:F0}s of {rule.HoldSeconds}s");
        if (state.LastFired is { } lf && now - lf < TimeSpan.FromSeconds(rule.CooldownSeconds)) return new AlertDecision(false, true, held, "in cooldown");

        state.LastFired = now;
        state.HeldSince = null; // re-arm only after conditions fail and hold again
        return new AlertDecision(true, false, held, null);
    }

    public static bool Holds(AlertCondition c, AlertValue v)
    {
        if (v.Number is { } n)
        {
            if (!double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var target)) return false;
            return c.Operator switch
            {
                AlertOperator.Gt or AlertOperator.CrossesAbove => n > target,
                AlertOperator.Gte => n >= target,
                AlertOperator.Lt or AlertOperator.CrossesBelow => n < target,
                AlertOperator.Lte => n <= target,
                AlertOperator.Eq => Math.Abs(n - target) < 1e-12,
                AlertOperator.Ne => Math.Abs(n - target) >= 1e-12,
                _ => false,
            };
        }
        if (v.Flag is { } f)
        {
            var target = c.Value.Equals("true", StringComparison.OrdinalIgnoreCase) || c.Value == "1";
            return c.Operator switch
            {
                AlertOperator.Eq or AlertOperator.CrossesAbove => f == target,
                AlertOperator.Ne => f != target,
                _ => false,
            };
        }
        if (v.Text is { } t)
        {
            var eq = string.Equals(t, c.Value, StringComparison.OrdinalIgnoreCase);
            return c.Operator switch
            {
                AlertOperator.Eq or AlertOperator.CrossesAbove => eq,
                AlertOperator.Ne => !eq,
                _ => false,
            };
        }
        return false;
    }

    public static string Describe(AlertCondition c, AlertValue v) => $"{c.Field} {Op(c.Operator)} {c.Value} (now {v})";

    public static string Op(AlertOperator o) => o switch
    {
        AlertOperator.Gt => ">", AlertOperator.Gte => "≥", AlertOperator.Lt => "<", AlertOperator.Lte => "≤",
        AlertOperator.Eq => "=", AlertOperator.Ne => "≠", AlertOperator.CrossesAbove => "crosses above", AlertOperator.CrossesBelow => "crosses below", _ => "?",
    };

    public static string? Validate(AlertRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return "Name is required.";
        if (rule.Conditions.Count == 0) return "At least one condition is required.";
        if (rule.HoldSeconds < 0 || rule.HoldSeconds > 86400) return "Hold must be between 0 and 86400 seconds.";
        if (rule.CooldownSeconds < 0 || rule.CooldownSeconds > 604800) return "Cooldown must be between 0 and 7 days.";
        foreach (var c in rule.Conditions)
        {
            if (AlertValues.IsNumeric(c.Field))
            {
                if (!double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return $"{c.Field} needs a numeric value.";
            }
            else if (c.Operator is not (AlertOperator.Eq or AlertOperator.Ne or AlertOperator.CrossesAbove)) return $"{c.Field} supports only = or ≠ (or 'crosses above' meaning 'becomes').";
            if (AlertValues.IsBoolean(c.Field) && c.Value is not ("true" or "false" or "1" or "0")) return $"{c.Field} needs true or false.";
        }
        if (rule.Channels.Contains("webhook") && (string.IsNullOrWhiteSpace(rule.WebhookUrl) || !Uri.TryCreate(rule.WebhookUrl, UriKind.Absolute, out var u) || u.Scheme != "https")) return "Webhook channel needs an https URL.";
        return null;
    }
}
