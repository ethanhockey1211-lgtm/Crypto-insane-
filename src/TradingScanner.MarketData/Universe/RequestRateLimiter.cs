namespace TradingScanner.MarketData.Universe;

/// <summary>Sliding-window limiter: at most N acquisitions per window. Used for REST warm-up.</summary>
public sealed class RequestRateLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Queue<long> _stamps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RequestRateLimiter(int maxPerWindow, TimeSpan window, TimeProvider? time = null)
    {
        _max = Math.Max(1, maxPerWindow);
        _window = window;
        _time = time ?? TimeProvider.System;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = _time.GetTimestamp();
                while (_stamps.Count > 0 && _time.GetElapsedTime(_stamps.Peek(), now) >= _window) _stamps.Dequeue();
                if (_stamps.Count < _max)
                {
                    _stamps.Enqueue(now);
                    return;
                }
                var wait = _window - _time.GetElapsedTime(_stamps.Peek(), now);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
