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
using TradingScanner.Infrastructure;
using TradingScanner.MarketData;
using TradingScanner.Signals;

var builder = WebApplication.CreateBuilder(args);

// PaaS convention (Render, Heroku, Fly): bind to the injected PORT when present.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port && int.TryParse(port, out var portNumber))
    builder.WebHost.UseUrls($"http://0.0.0.0:{portNumber}");

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; o.TimestampFormat = "O"; });
}

builder.Services.AddMarketData(builder.Configuration);
builder.Services.AddAnalytics(builder.Configuration);
builder.Services.AddPostgresPersistence(builder.Configuration); // before AddSignals: replaces the in-memory stores when configured
builder.Services.AddSignals(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<MarketBroadcaster>();
builder.Services.AddSingleton<IMarketEventObserver>(sp => sp.GetRequiredService<MarketBroadcaster>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MarketBroadcaster>());

builder.Services.AddHostedService<ScannerBroadcaster>();
builder.Services.AddSingleton<TradingScanner.Backtest.IHistoricalCandleSource, TradingScanner.Api.Endpoints.ProviderCandleSource>();

// AI explanation layer: only narrates engine output, enabled only when a key is configured (env ANTHROPIC_API_KEY or Anthropic:ApiKey).
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection(AnthropicOptions.SectionName));
builder.Services.AddSingleton<TradingScanner.Signals.Explain.IExplanationService>(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>().Value;
    var key = string.IsNullOrWhiteSpace(o.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : o.ApiKey;
    TradingScanner.Signals.Explain.IExplanationModel? model = string.IsNullOrWhiteSpace(key) ? null : new AnthropicExplanationModel(o with { ApiKey = key }, sp.GetRequiredService<ILogger<AnthropicExplanationModel>>());
    return new TradingScanner.Signals.Explain.ExplanationService(model, sp.GetRequiredService<TimeProvider>());
});

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

// The dashboard (web/, built with NEXT_OUTPUT=export) is served from wwwroot when present, so the API's own
// origin is the whole product. Without it (tests, bare API deployments) "/" still points at the status readout.
// Static files must run BEFORE routing: with the implicit UseRouting at the head of the pipeline, the catch-all
// fallback endpoint would match every asset path first and the static file middleware would skip them.
var dashboardIndex = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
var hasDashboard = File.Exists(dashboardIndex);
if (hasDashboard)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.UseRouting();
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
app.MapAlertEndpoints();
app.MapPaperEndpoints();
app.MapPerformanceEndpoints();
app.MapBacktestEndpoints();
app.MapExplainEndpoints();
app.MapHub<MarketHub>("/hubs/market");
if (hasDashboard)
    app.MapFallbackToFile("{*path:regex(^(?!api/|hubs/|health/).*$)}", "index.html");
else
    app.MapGet("/", () => Results.Redirect("/api/system/feed"));

app.Run();

public partial class Program { }
