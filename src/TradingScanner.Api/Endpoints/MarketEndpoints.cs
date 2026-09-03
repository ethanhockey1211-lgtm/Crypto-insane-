using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using TradingScanner.Api.Contracts;
using TradingScanner.Api.Services;
using TradingScanner.Analytics;
using TradingScanner.Signals;
using TradingScanner.Signals.Breakouts;
using TradingScanner.Signals.Scanner;
using TradingScanner.Signals.Tape;
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

        market.MapGet("/{symbol}/analytics", Results<Ok<AnalyticsProjection>, NotFound> (string symbol, IMarketStateReader reader, IAnalyticsReader analytics, TimeProvider time) =>
        {
            if (!TryParseSymbol(symbol, out var sym)) return TypedResults.NotFound();
            var snapshot = analytics.GetSnapshot(sym);
            if (snapshot is null) return TypedResults.NotFound();
            var quote = reader.GetQuote(sym);
            var price = quote is not null ? (double)quote.Price : snapshot.Momentum?.LastClose ?? snapshot.Timeframes.FirstOrDefault()?.Close ?? 0d;
            return TypedResults.Ok(snapshot.Project(price, time.GetUtcNow()));
        });

        market.MapGet("/{symbol}/breakouts", Results<Ok<BreakoutAnalysis>, NotFound> (string symbol, ISignalsReader signals) =>
        {
            if (!TryParseSymbol(symbol, out var sym)) return TypedResults.NotFound();
            var analysis = signals.GetBreakouts(sym);
            return analysis is null ? TypedResults.NotFound() : TypedResults.Ok(analysis);
        });

        var scanner = app.MapGroup("/api/scanner").RequireRateLimiting("api");
        scanner.MapGet("/", Results<Ok<ScannerStreamDto>, NotFound> (IScannerReader reader) =>
            reader.Latest is { } s ? TypedResults.Ok(ScannerBroadcaster.ToStream(s)) : TypedResults.NotFound());
        scanner.MapGet("/market", Results<Ok<MarketContext>, NotFound> (IScannerReader reader) =>
            reader.Latest is { } s ? TypedResults.Ok(s.Market) : TypedResults.NotFound());
        scanner.MapGet("/tape", (IScannerReader reader, int? limit) => Results.Ok(reader.Tape.Recent(Math.Clamp(limit ?? 100, 1, 300))));
        scanner.MapGet("/{symbol}", Results<Ok<Opportunity>, NotFound> (string symbol, IScannerReader reader) =>
        {
            if (!TryParseSymbol(symbol, out var sym)) return TypedResults.NotFound();
            var o = reader.Get(sym);
            return o is null ? TypedResults.NotFound() : TypedResults.Ok(o);
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
