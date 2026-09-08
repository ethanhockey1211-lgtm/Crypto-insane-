using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using TradingScanner.Analytics;
using TradingScanner.Backtest;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.Signals;

namespace TradingScanner.Api.Endpoints;

public sealed record BacktestApiRequest(List<string> Symbols, int Days = 3, int FeeBps = 10, int SlippageBps = 5, int SpreadBps = 4, double RecordThreshold = 60, bool IncludeBtc = true);

/// <summary>1m history from the live provider's REST API (rate limited by the provider itself).</summary>
public sealed class ProviderCandleSource : IHistoricalCandleSource
{
    private readonly IMarketDataProvider _provider;
    public ProviderCandleSource(IMarketDataProvider provider) => _provider = provider;
    public async Task<IReadOnlyList<Candle>> GetM1Async(Symbol symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var candles = await _provider.GetHistoricalCandlesAsync(symbol, Timeframe.M1, from, to, ct);
        // Some providers silently truncate history (Kraken returns at most 720 recent OHLC rows).
        // Never label a few hours of data as a multi-day backtest, including its warm-up period.
        if (candles.Count == 0 || candles.Min(c => c.OpenTime) > from.AddMinutes(1)
            || candles.Max(c => c.CloseTime) < to.AddMinutes(-2))
            throw new InvalidOperationException($"{_provider.Exchange} did not supply the requested history and warm-up window. "
                + "Kraken public 1m OHLC covers only about 12 recent hours; multi-day backtests require an archived history source.");
        return candles;
    }
}

public static class BacktestEndpoints
{
    public static IEndpointRouteBuilder MapBacktestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/backtest", async Task<Results<Ok<BacktestResult>, BadRequest<string>>> (BacktestApiRequest req, IHistoricalCandleSource source, IOptions<AnalyticsOptions> analytics, IOptions<SignalsOptions> signals, ILoggerFactory logs, TimeProvider time, CancellationToken ct) =>
        {
            if (req.Symbols is null || req.Symbols.Count == 0) return TypedResults.BadRequest("At least one symbol is required.");
            if (req.Symbols.Count > 5) return TypedResults.BadRequest("At most 5 symbols per run; history is fetched over REST.");
            if (req.Days is < 1 or > 14) return TypedResults.BadRequest("Days must be between 1 and 14.");
            var symbols = req.Symbols.Select(s => new Symbol(s)).ToList();
            if (req.IncludeBtc && !symbols.Contains(new Symbol("BTC-USD"))) symbols.Add(new Symbol("BTC-USD"));
            var to = time.GetUtcNow();
            var from = to.AddDays(-req.Days);
            var warmFrom = from.AddHours(-6);
            var data = new Dictionary<Symbol, IReadOnlyList<Candle>>();
            foreach (var s in symbols)
            {
                try { data[s] = await source.GetM1Async(s, warmFrom, to, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return TypedResults.BadRequest($"History for {s} unavailable: {ex.Message}"); }
            }
            var runner = new BacktestRunner(analytics.Value, signals.Value, logs.CreateLogger<BacktestRunner>());
            var result = runner.Run(data, new BacktestRequest(symbols, from, to, new CostModel(req.FeeBps, req.SlippageBps, req.SpreadBps, 1), req.RecordThreshold));
            return TypedResults.Ok(result);
        }).RequireRateLimiting("api");
        return app;
    }
}
