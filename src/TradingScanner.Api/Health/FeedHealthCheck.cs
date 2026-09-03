using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Engine;

namespace TradingScanner.Api.Health;

/// <summary>Ready only when the feed is connected and the engine has consumed an event recently.</summary>
public sealed class FeedHealthCheck : IHealthCheck
{
    private readonly IMarketStateReader _reader;
    private readonly MarketDataOptions _options;
    private readonly TimeProvider _time;

    public FeedHealthCheck(IMarketStateReader reader, IOptions<MarketDataOptions> options, TimeProvider time)
    {
        _reader = reader;
        _options = options.Value;
        _time = time;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = _reader.FeedStatus;
        var now = _time.GetUtcNow();
        var lastEventAge = _reader.LastEventAt is { } le ? now - le : (TimeSpan?)null;
        var data = new Dictionary<string, object>
        {
            ["feedStatus"] = status.ToString(),
            ["lastEventAgeMs"] = lastEventAge?.TotalMilliseconds ?? -1,
            ["symbols"] = _reader.Symbols.Count,
            ["engineErrors"] = _reader.EngineErrors,
        };

        if (status == FeedStatus.Connected && lastEventAge is { } age && age <= _options.StaleQuoteThreshold)
            return Task.FromResult(HealthCheckResult.Healthy("Feed connected", data));
        if (status is FeedStatus.Degraded or FeedStatus.Reconnecting)
            return Task.FromResult(HealthCheckResult.Degraded($"Feed {status}", data: data));
        return Task.FromResult(HealthCheckResult.Unhealthy($"Feed {status}", data: data));
    }
}
