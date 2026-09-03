using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using TradingScanner.Api.Endpoints;
using TradingScanner.Api.Health;
using TradingScanner.Api.Hubs;
using TradingScanner.Api.Services;
using System.Text.Json.Serialization;
using TradingScanner.Analytics;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData;
using TradingScanner.Signals;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; o.TimestampFormat = "O"; });
}

builder.Services.AddMarketData(builder.Configuration);
builder.Services.AddAnalytics(builder.Configuration);
builder.Services.AddSignals(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<MarketBroadcaster>();
builder.Services.AddSingleton<IMarketEventObserver>(sp => sp.GetRequiredService<MarketBroadcaster>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MarketBroadcaster>());

builder.Services.AddHostedService<ScannerBroadcaster>();

builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 64 * 1024)
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:3000"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddHealthChecks()
    .AddCheck<FeedHealthCheck>("feed", tags: ["ready"]);

var app = builder.Build();
app.Services.GetRequiredService<SignalsEngine>(); // subscribe to analytics before the first bar closes

app.UseCors();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), e.Value.Description, e.Value.Data }),
        });
    },
});

app.MapMarketEndpoints();
app.MapHub<MarketHub>("/hubs/market");
app.MapGet("/", () => Results.Redirect("/api/system/feed"));

app.Run();

public partial class Program { }
