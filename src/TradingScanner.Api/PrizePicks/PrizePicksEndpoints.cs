using Microsoft.Extensions.Options;

namespace TradingScanner.Api.PrizePicks;

public static class PrizePicksEndpoints
{
    public static IServiceCollection AddPrizePicks(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PrizePicksOptions>(configuration.GetSection("PrizePicks"));
        services.AddHttpClient("prizepicks-odds", c => c.Timeout = TimeSpan.FromSeconds(12))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton(sp => new PrizePicksFeed(sp.GetRequiredService<IHttpClientFactory>().CreateClient("prizepicks-odds"),
            sp.GetRequiredService<IOptions<PrizePicksOptions>>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
    public static IEndpointRouteBuilder MapPrizePicks(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/prizepicks").RequireRateLimiting("api");
        group.MapGet("/status", (HttpContext context, PrizePicksFeed feed) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { configured = feed.Configured, source = "The Odds API · PrizePicks", sports = PrizePicksFeed.Sports.Keys });
        });
        group.MapGet("/board", async (HttpContext context, string? sport, PrizePicksFeed feed) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var response = await feed.ScanAsync(sport ?? "americanfootball_nfl", context.RequestAborted);
            return Results.Json(response.Body, statusCode: response.StatusCode);
        });
        return app;
    }
}
