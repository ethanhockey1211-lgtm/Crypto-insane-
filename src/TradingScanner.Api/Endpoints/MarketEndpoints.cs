using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using TradingScanner.Api.Contracts;
using TradingScanner.Api.Services;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Engine;
using TradingScanner.MarketData.Metrics;

namespace TradingScanner.Api.Endpoints;

public static class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var market = app.MapGroup("/api/market").RequireRateLimiting("api");

        market.MapGet("/symbols", (IMarketStateReader reader, UniverseState universe, IOptions<MarketDataOptions> options, TimeProvider time) =>
        {
            var now = time.GetUtcNow();
            var threshold = options.Value.StaleQuoteThreshold;
            var list = universe.Symbols
                .Select(s => reader.Get(s))
                .Where(s => s is not null)
                .Select(s => ToSummary(s!, now, threshold))
                .ToList();
            return Results.Ok(list);
        });

        market.MapGet("/{symbol}/quote", Results<Ok<SymbolSummaryDto>, NotFound> (string symbol, IMarketStateReader reader, IOptions<MarketDataOptions> options, TimeProvider time) =>
        {
            if (!TryParseSymbol(symbol, out var sym)) return TypedResults.NotFound();
            var state = reader.Get(sym);
            return state is null ? TypedResults.NotFound() : TypedResults.Ok(ToSummary(state, time.GetUtcNow(), options.Value.StaleQuoteThreshold));
        });

        market.MapGet("/{symbol}/candles", Results<Ok<CandlesResponse>, NotFound, BadRequest<string>> (string symbol, string? tf, int? limit, IMarketStateReader reader) =>
        {
            if (!TryParseSymbol(symbol, out var sym)) return TypedResults.NotFound();
            if (!TimeframeExtensions.TryParse(tf ?? "1m", out var timeframe)) return TypedResults.BadRequest($"Unknown timeframe '{tf}'. Use 1m,3m,5m,15m,30m,1h,4h.");
            var n = Math.Clamp(limit ?? 300, 1, 2000);
            var snap = reader.GetCandles(sym, timeframe, n);
            if (snap is null) return TypedResults.NotFound();
            var candles = new CandleDto[snap.Closed.Length];
            for (var i = 0; i < candles.Length; i++) candles[i] = CandleDto.From(snap.Closed[i]);
            return TypedResults.Ok(new CandlesResponse(sym.Value, timeframe.Label(), candles, snap.Forming is { } f ? CandleDto.From(f) : null));
        });

        var system = app.MapGroup("/api/system").RequireRateLimiting("api");
        system.MapGet("/feed", (MarketBroadcaster broadcaster) => Results.Ok(broadcaster.BuildFeedStatus()));
        system.MapGet("/metrics", (MarketDataMetrics metrics) => Results.Ok(metrics.Snapshot()));
        return app;
    }

    private static bool TryParseSymbol(string raw, out Symbol symbol)
    {
        try { symbol = new Symbol(raw); return true; }
        catch (ArgumentException) { symbol = default; return false; }
    }

    private static SymbolSummaryDto ToSummary(SymbolState s, DateTimeOffset now, TimeSpan threshold)
    {
        var q = s.Quote;
        var st = s.Stats;
        decimal? change = st is not null && st.Open24h > 0 && q is not null ? (q.Price - st.Open24h) / st.Open24h * 100m : null;
        return new SymbolSummaryDto(
            s.Symbol.Value,
            q is null ? null : QuoteDto.From(q, now, threshold),
            st?.Open24h, st?.High24h, st?.Low24h, st?.Volume24hBase,
            change is { } c ? Math.Round(c, 2) : null,
            s.TradesSeen,
            s.HistoryLoaded);
    }
}
