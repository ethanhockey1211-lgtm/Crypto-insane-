using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.Stocks;

public static class StockEndpoints
{
    public const string AccessHeader = "X-Stock-Access-Token";
    public const string HttpClientName = "stock-alpaca-iex";

    public static IServiceCollection AddStockScanner(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StockScannerOptions>(configuration.GetSection(StockScannerOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .RedactLoggedHeaders(_ => true);
        services.AddSingleton(sp => new AlpacaStockScanner(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<IOptions<StockScannerOptions>>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stocks").RequireRateLimiting("api");
        group.MapGet("/status", (HttpContext context, AlpacaStockScanner scanner) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(scanner.GetStatus());
        });
        group.MapGet("/scan", async (HttpContext context, string? symbols, AlpacaStockScanner scanner) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var values = context.Request.Headers[AccessHeader];
            var token = values.Count == 1 ? values[0] : null;
            var result = await scanner.ScanAsync(symbols, token, context.RequestAborted);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });
        return app;
    }
}
