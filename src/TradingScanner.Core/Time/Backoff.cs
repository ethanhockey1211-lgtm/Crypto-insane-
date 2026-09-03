namespace TradingScanner.Core.Time;

/// <summary>Exponential backoff with full jitter, capped. Deterministic when given a seeded Random.</summary>
public sealed class Backoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly Random _random;
    private int _attempt;

    public Backoff(TimeSpan initial, TimeSpan max, Random? random = null)
    {
        _initial = initial;
        _max = max;
        _random = random ?? Random.Shared;
    }

    public int Attempt => _attempt;

    public TimeSpan Next()
    {
        var expMs = _initial.TotalMilliseconds * Math.Pow(2, Math.Min(_attempt, 16));
        var capMs = Math.Min(expMs, _max.TotalMilliseconds);
        _attempt++;
        // full jitter: uniform in [initial/2, cap]
        var floor = Math.Min(_initial.TotalMilliseconds / 2, capMs);
        return TimeSpan.FromMilliseconds(floor + _random.NextDouble() * (capMs - floor));
    }

    public void Reset() => _attempt = 0;
}
