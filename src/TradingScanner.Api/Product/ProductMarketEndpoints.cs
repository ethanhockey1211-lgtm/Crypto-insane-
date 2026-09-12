using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingScanner.Api.Contracts;
using TradingScanner.Api.Services;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Api.Product;

/// <summary>Customer projections of the existing shared scanner; never a second analysis engine.</summary>
public static class ProductMarketEndpoints
{
    private const string ScopeNotice = "Public spot/USD listings from the configured exchange. Listings do not verify availability, fees, or eligibility in your exchange account.";

    public static IEndpointRouteBuilder MapProductMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product").RequireRateLimiting("api");
        group.MapGet("/overview", (IOptions<ProductOptions> product, IOptions<ScannerOptions> scannerOptions,
            IScannerReader reader, MarketBroadcaster broadcaster, TimeProvider time, IConfiguration config) =>
        {
            var now = time.GetUtcNow();
            var approved = product.Value.MarketDataApproved;
            var snapshot = approved ? reader.Latest : null;
            var feed = approved ? broadcaster.BuildFeedStatus() : null;
            var fresh = snapshot is not null && IsFresh(snapshot.At, now, scannerOptions.Value.StaleQuoteThreshold);
            var available = approved && fresh && feed?.Live == true;
            var limit = Math.Clamp(config.GetValue("Product:Limits:FreeOverviewMarkets", 3), 1, 5);
            // Fixed public sample: no query parameter can be used to enumerate paid analyses.
            var preview = snapshot?.Opportunities.Where(o => new[] { "BTC-USD", "ETH-USD", "SOL-USD", "XRP-USD", "ADA-USD" }.Contains(o.Symbol.Value))
                .OrderBy(o => o.Symbol.Value, StringComparer.Ordinal).Take(limit)
                .Select(o => ScannerRowDto.From(AssessForCustomer(o, snapshot.Market, 40, 10, scannerOptions.Value, now))).ToArray() ?? [];
            return Results.Ok(new
            {
                mode = !approved ? "demo" : available ? "live" : "disconnected",
                at = snapshot?.At,
                feed = new { live = available, status = !approved ? "Demo" : feed?.Status ?? "Starting", exchange = config["MarketData:Provider"] ?? "kraken", historyLoaded = feed?.History.Loaded ?? 0, historyTotal = feed?.History.Total ?? 0 },
                market = snapshot?.Market,
                rows = preview,
                examples = Array.Empty<object>(),
                reason = !approved ? "Explore illustrative examples. Commercial market-data permission is pending; live market alerts are disabled."
                    : !available ? "The market feed is starting, disconnected, or stale. Alerts are paused until fresh data and complete analysis are available."
                    : snapshot!.Opportunities.Any(o => o.Execution?.Status == "Watch" && !o.Quality.Stale) ? "Conditional setups remain subject to their confirmation and invalidation checks."
                    : "No setups currently pass the engine's evidence, freshness, liquidity, risk, and cost checks. Monitoring continues without loosening the criteria.",
                marketScope = ScopeNotice,
                limits = new { freeOverviewMarkets = limit },
            });
        });

        group.MapGet("/scanner", async (HttpContext http, ProductAccess access, ProductDbContext db,
            IOptions<ProductOptions> product, IOptions<ScannerOptions> options, IScannerReader reader, TimeProvider time, CancellationToken ct) =>
        {
            var denied = await CheckAccess(http, access, product.Value, ct);
            if (denied is not null) return denied;
            var snapshot = reader.Latest;
            if (snapshot is null) return Results.Problem("Market history is warming up. No analysis is available yet.", statusCode: 503);
            var prefs = await db.Preferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == ProductAccess.UserId(http.User), ct);
            var now = time.GetUtcNow();
            var rows = snapshot.Opportunities.Select(o => ScannerRowDto.From(AssessForCustomer(o, snapshot.Market,
                (double)(prefs?.TakerFeeBps ?? 40), (double)(prefs?.SlippageBps ?? 10), options.Value, now))).ToList();
            return Results.Ok(new ScannerStreamDto(snapshot.At, snapshot.Market, rows, snapshot.Universe, snapshot.CycleMs));
        });

        group.MapGet("/setup/{symbol}", async (string symbol, HttpContext http, ProductAccess access, ProductDbContext db,
            IOptions<ProductOptions> product, IOptions<ScannerOptions> options, IScannerReader reader, TimeProvider time, CancellationToken ct) =>
        {
            var denied = await CheckAccess(http, access, product.Value, ct);
            if (denied is not null) return denied;
            Symbol parsed;
            try { parsed = new Symbol(symbol); } catch (ArgumentException) { return Results.NotFound(); }
            var snapshot = reader.Latest;
            var opportunity = snapshot?.Get(parsed);
            if (snapshot is null || opportunity is null) return Results.NotFound(new { detail = "No current analysis is available for this market. It may still be warming up." });
            var prefs = await db.Preferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == ProductAccess.UserId(http.User), ct);
            return Results.Ok(AssessForCustomer(opportunity, snapshot.Market, (double)(prefs?.TakerFeeBps ?? 40),
                (double)(prefs?.SlippageBps ?? 10), options.Value, time.GetUtcNow()));
        });
        return app;
    }

    private static async Task<IResult?> CheckAccess(HttpContext http, ProductAccess access, ProductOptions options, CancellationToken ct)
    {
        var userId = ProductAccess.UserId(http.User);
        if (userId is null) return Results.Unauthorized();
        if (!await access.IsProAsync(userId, ct)) return Results.Json(new { detail = "Pro is required for the full scanner." }, statusCode: 403);
        if (!options.MarketDataApproved) return Results.Problem("Commercial market-data permission is pending. Explore the labeled free examples while live scanning is unavailable.", statusCode: 503);
        return null;
    }

    public static bool IsFresh(DateTimeOffset at, DateTimeOffset now, TimeSpan threshold) => now - at <= threshold && now - at >= TimeSpan.FromSeconds(-2);

    /// <summary>Changes customer costs only. Evidence, score, setup geometry and engine thresholds stay intact.</summary>
    public static Opportunity AssessForCustomer(Opportunity opportunity, MarketContext market, double feeBps,
        double slippageBps, ScannerOptions options, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var elapsed = (long)Math.Max(0, (at - opportunity.At).TotalMilliseconds);
        var age = opportunity.Quality.AgeMs + elapsed;
        var stale = opportunity.Quality.Stale || age > options.StaleQuoteThreshold.TotalMilliseconds
            || age < -2000 || !IsFresh(opportunity.At, at, options.StaleQuoteThreshold)
            || !IsFresh(market.At, at, options.StaleQuoteThreshold);
        var adjusted = opportunity with { Quality = opportunity.Quality with { Stale = stale, AgeMs = age } };
        var cfg = options.Execution;
        return adjusted with { Execution = ExecutionAssessor.Assess(adjusted, market, new ExecutionConfig
        {
            FeeBps = feeBps, SlippageBps = slippageBps,
            MinScore = Math.Max(cfg.MinScore, options.SetupScoreThreshold),
            MaxSpreadBps = cfg.MaxSpreadBps, MinNetRewardRatio = cfg.MinNetRewardRatio,
            MinVolume24hQuote = cfg.MinVolume24hQuote,
        }) };
    }
}
