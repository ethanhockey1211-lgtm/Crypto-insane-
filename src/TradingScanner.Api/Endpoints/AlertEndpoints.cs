using Microsoft.AspNetCore.Http.HttpResults;
using TradingScanner.Signals.Alerts;

namespace TradingScanner.Api.Endpoints;

public sealed record AlertRuleRequest(string Name, bool Enabled, string? Symbol, List<AlertCondition> Conditions, int HoldSeconds, int CooldownSeconds, List<string> Channels, string? WebhookUrl, bool RepeatWhileTrue = false);

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/alerts").RequireRateLimiting("api");

        g.MapGet("/", async (IAlertRepository repo, CancellationToken ct) => Results.Ok(await repo.ListRulesAsync(ct)));
        g.MapGet("/events", async (IAlertRepository repo, int? limit, CancellationToken ct) => Results.Ok(await repo.ListEventsAsync(Math.Clamp(limit ?? 100, 1, 1000), ct)));
        g.MapGet("/fields", () => Results.Ok(new
        {
            fields = Enum.GetNames<AlertField>().Select(f => new { name = f, kind = AlertValues.IsNumeric(Enum.Parse<AlertField>(f)) ? "number" : AlertValues.IsBoolean(Enum.Parse<AlertField>(f)) ? "boolean" : "text" }),
            operators = Enum.GetNames<AlertOperator>(),
        }));

        g.MapPost("/", async Task<Results<Created<AlertRule>, BadRequest<string>>> (AlertRuleRequest req, IAlertRepository repo, AlertService service, TimeProvider time, CancellationToken ct) =>
        {
            var rule = ToRule(Guid.NewGuid(), req, time.GetUtcNow(), null);
            if (AlertEvaluator.Validate(rule) is { } error) return TypedResults.BadRequest(error);
            await repo.UpsertRuleAsync(rule, ct);
            await service.ReloadAsync(ct);
            return TypedResults.Created($"/api/alerts/{rule.Id}", rule);
        });

        g.MapPut("/{id:guid}", async Task<Results<Ok<AlertRule>, NotFound, BadRequest<string>>> (Guid id, AlertRuleRequest req, IAlertRepository repo, AlertService service, CancellationToken ct) =>
        {
            var existing = await repo.GetRuleAsync(id, ct);
            if (existing is null) return TypedResults.NotFound();
            var rule = ToRule(id, req, existing.CreatedAt, existing.LastFiredAt);
            if (AlertEvaluator.Validate(rule) is { } error) return TypedResults.BadRequest(error);
            await repo.UpsertRuleAsync(rule, ct);
            await service.ReloadAsync(ct);
            return TypedResults.Ok(rule);
        });

        g.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, IAlertRepository repo, AlertService service, CancellationToken ct) =>
        {
            if (!await repo.DeleteRuleAsync(id, ct)) return TypedResults.NotFound();
            await service.ReloadAsync(ct);
            return TypedResults.NoContent();
        });
        return app;
    }

    private static AlertRule ToRule(Guid id, AlertRuleRequest r, DateTimeOffset createdAt, DateTimeOffset? lastFired) =>
        new(id, r.Name?.Trim() ?? "", r.Enabled, string.IsNullOrWhiteSpace(r.Symbol) ? null : r.Symbol.Trim().ToUpperInvariant(),
            r.Conditions ?? [], r.HoldSeconds, r.CooldownSeconds, (r.Channels ?? []).Select(c => c.ToLowerInvariant()).Distinct().ToList(), string.IsNullOrWhiteSpace(r.WebhookUrl) ? null : r.WebhookUrl.Trim(), createdAt, lastFired, r.RepeatWhileTrue);
}
