using TradingScanner.Signals.Performance;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Api.Endpoints;

public static class PerformanceEndpoints
{
    public static IEndpointRouteBuilder MapPerformanceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/performance").RequireRateLimiting("api");
        g.MapGet("/", async (ISignalRepository repo, IScannerReader scanner, TimeProvider time, CancellationToken ct) =>
        {
            var items = await repo.ListAsync(20_000, null, ct);
            var version = scanner.Latest?.Opportunities.FirstOrDefault()?.Breakdown.ConfigVersion ?? 0;
            return Results.Ok(SignalTracker.Report(items, time.GetUtcNow(), version));
        });
        g.MapGet("/signals", async (ISignalRepository repo, int? limit, string? symbol, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(Math.Clamp(limit ?? 100, 1, 2000), string.IsNullOrWhiteSpace(symbol) ? null : symbol.ToUpperInvariant(), ct)));
        return app;
    }
}
