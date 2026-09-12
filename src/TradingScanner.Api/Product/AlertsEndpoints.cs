using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Api.Product;

public static class ProductAlertEndpoints
{
    public static IEndpointRouteBuilder MapProductAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var alerts = endpoints.MapGroup("/api/product/alerts").RequireAuthorization().RequireRateLimiting("api");
        alerts.AddEndpointFilter(async (context, next) =>
            await context.HttpContext.RequestServices.GetRequiredService<ProductAccess>().IsProAsync(ProductAccess.UserId(context.HttpContext.User)!, context.HttpContext.RequestAborted)
                ? await next(context) : Results.Json(new { error = "Pro is required for alert rules and history." }, statusCode: 403));
        alerts.AddEndpointFilter(SerializeWrites);
        alerts.MapGet("/rules", async (ClaimsPrincipal user, ProductDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AlertRules.Where(x => x.UserId == ProductAccess.UserId(user)).OrderBy(x => x.CreatedAt).ToListAsync(ct);
            return Results.Ok(rows.Select(RuleView));
        });
        alerts.MapPost("/rules", (AlertRuleRequest request, ClaimsPrincipal user, ProductDbContext db, IOptions<ProductOptions> options, IOptions<ScannerOptions> scanner, TimeProvider time, CancellationToken ct) =>
            SaveRule(null, request, user, db, options.Value, scanner.Value, time, ct));
        alerts.MapPut("/rules/{id:guid}", (Guid id, AlertRuleRequest request, ClaimsPrincipal user, ProductDbContext db, IOptions<ProductOptions> options, IOptions<ScannerOptions> scanner, TimeProvider time, CancellationToken ct) =>
            SaveRule(id, request, user, db, options.Value, scanner.Value, time, ct));
        alerts.MapDelete("/rules/{id:guid}", async (Guid id, ClaimsPrincipal user, ProductDbContext db, CancellationToken ct) =>
        {
            var row = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == id && x.UserId == ProductAccess.UserId(user), ct);
            if (row is null) return Results.NotFound();
            db.AlertRules.Remove(row);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        alerts.MapGet("/history", async (ClaimsPrincipal user, ProductDbContext db, IOptions<ProductOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var since = time.GetUtcNow().AddDays(-Math.Clamp(options.Value.Limits.AlertHistoryDays, 1, 3650));
            var rows = await db.AlertRecords.AsNoTracking().Where(x => x.UserId == ProductAccess.UserId(user) && x.IssuedAt >= since)
                .OrderByDescending(x => x.IssuedAt).Take(200).ToListAsync(ct);
            var ids = rows.Select(x => x.Id).ToArray();
            var deliveries = await db.PushDeliveries.AsNoTracking().Where(x => ids.Contains(x.AlertId)).ToListAsync(ct);
            var outcomes = await db.AlertOutcomes.AsNoTracking().Where(x => ids.Contains(x.AlertId)).ToDictionaryAsync(x => x.AlertId, ct);
            return Results.Ok(rows.Select(x => HistoryView(x, deliveries.Where(d => d.AlertId == x.Id), outcomes.GetValueOrDefault(x.Id), time.GetUtcNow())));
        });
        alerts.MapGet("/history/{id:guid}", async (Guid id, ClaimsPrincipal user, ProductDbContext db, IOptions<ProductOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var since = time.GetUtcNow().AddDays(-Math.Clamp(options.Value.Limits.AlertHistoryDays, 1, 3650));
            var row = await db.AlertRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == ProductAccess.UserId(user) && x.IssuedAt >= since, ct);
            if (row is null) return Results.NotFound();
            var deliveries = await db.PushDeliveries.AsNoTracking().Where(x => x.AlertId == id).ToListAsync(ct);
            var outcome = await db.AlertOutcomes.AsNoTracking().SingleOrDefaultAsync(x => x.AlertId == id, ct);
            return Results.Ok(HistoryView(row, deliveries, outcome, time.GetUtcNow()));
        });

        var push = endpoints.MapGroup("/api/product/push").RequireAuthorization().RequireRateLimiting("api");
        push.AddEndpointFilter(SerializeWrites);
        push.MapGet("/config", (IOptions<ProductPushOptions> options, IOptions<ScannerOptions> scanner) => Results.Ok(new
        {
            enabled = options.Value.IsConfigured,
            publicKey = options.Value.IsConfigured ? options.Value.PublicKey : null,
            ruleLimits = new { minimumScore = Math.Max(scanner.Value.SetupScoreThreshold, scanner.Value.Execution.MinScore), maximumNameLength = 80, maximumHoldSeconds = 3600, minimumCooldownMinutes = 5, maximumCooldownMinutes = 10080 },
            limitations = "Requires HTTPS and a supported browser. On iPhone/iPad (iOS/iPadOS 16.4+), install to the Home Screen and open the installed app first. Enable notifications with the device button. Delivery may be delayed or blocked by browser, network, battery, focus, or OS settings; it is never guaranteed.",
        }));
        push.MapGet("/devices", async (ClaimsPrincipal user, ProductDbContext db, CancellationToken ct) => Results.Ok(
            await db.PushDevices.AsNoTracking().Where(x => x.UserId == ProductAccess.UserId(user)).OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.Id, x.DeviceName, x.Enabled, x.CreatedAt, x.LastAcceptedAt, x.LastTestAt, x.LastError }).ToListAsync(ct)));
        push.MapPost("/subscriptions", async (PushSubscriptionRequest request, ClaimsPrincipal user, ProductDbContext db, ProductAccess access, IOptions<ProductPushOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var userId = ProductAccess.UserId(user)!;
            if (!await access.IsProAsync(userId, ct)) return ProRequired();
            if (!options.Value.IsConfigured) return Results.Json(new { error = "Push is awaiting server VAPID configuration." }, statusCode: 503);
            if (!PushEndpointPolicy.IsAllowed(request.Endpoint) || request.Keys is null || !PushEndpointPolicy.ValidKeys(request.Keys))
                return Results.BadRequest(new { error = "Use a valid browser-issued subscription from a supported push provider." });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Endpoint)));
            var existing = await db.PushDevices.SingleOrDefaultAsync(x => x.EndpointHash == hash, ct);
            // A browser subscription belongs to one account. Never silently transfer another customer's device.
            if (existing is not null && existing.UserId != userId)
                return Results.Conflict(new { error = "This browser subscription is linked to another account. Disable notifications there or reset this browser subscription first." });
            if ((existing is null || !existing.Enabled) && await db.PushDevices.CountAsync(x => x.UserId == userId && x.Enabled, ct) >= Math.Clamp(options.Value.MaximumDevices, 1, 20))
                return Results.BadRequest(new { error = "Device limit reached. Disable an older device first." });
            var row = existing ?? new CustomerPushDevice { UserId = userId, EndpointHash = hash, CreatedAt = time.GetUtcNow() };
            row.Endpoint = request.Endpoint;
            row.P256dh = request.Keys.P256dh;
            row.Auth = request.Keys.Auth;
            row.DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? "This device" : request.DeviceName.Trim()[..Math.Min(request.DeviceName.Trim().Length, 80)];
            row.Enabled = true;
            row.LastError = null;
            if (existing is null) db.PushDevices.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { row.Id, row.DeviceName, row.Enabled });
        });
        push.MapDelete("/subscriptions/{id:guid}", async (Guid id, ClaimsPrincipal user, ProductDbContext db, CancellationToken ct) =>
        {
            var row = await db.PushDevices.SingleOrDefaultAsync(x => x.Id == id && x.UserId == ProductAccess.UserId(user), ct);
            if (row is null) return Results.NotFound();
            row.Enabled = false;
            // Remove endpoint credentials while retaining delivery diagnostics.
            row.Endpoint = row.P256dh = row.Auth = "";
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        push.MapPost("/test", async (PushTestRequest request, ClaimsPrincipal user, ProductDbContext db, ProductAccess access, IOptions<ProductPushOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            var userId = ProductAccess.UserId(user)!;
            if (!await access.IsProAsync(userId, ct)) return ProRequired();
            if (!options.Value.IsConfigured) return Results.Json(new { error = "Push is awaiting server VAPID configuration." }, statusCode: 503);
            var device = await db.PushDevices.SingleOrDefaultAsync(x => x.Id == request.DeviceId && x.UserId == userId && x.Enabled, ct);
            if (device is null) return Results.NotFound();
            var now = time.GetUtcNow();
            if (device.LastTestAt is { } last && now - last < TimeSpan.FromMinutes(1))
                return Results.Json(new { error = "Wait one minute between device tests." }, statusCode: 429);
            device.LastTestAt = now;
            var record = new CustomerAlertRecord { UserId = userId, RuleName = "Device test", IssuedAt = now, ExpiresAt = now.AddMinutes(5), IsTest = true, Status = "queued", Message = "Your monitoring notifications are set up on this device. This is a test, not a market alert." };
            db.AlertRecords.Add(record);
            db.PushDeliveries.Add(new CustomerPushDelivery { AlertId = record.Id, DeviceId = device.Id, NextAttemptAt = now });
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/product/alerts/history/{record.Id}", new { id = record.Id, status = "queued", detailUrl = DetailUrl(record.Id) });
        });
        return endpoints;
    }

    private static IResult ProRequired() => Results.Json(new { error = "Pro is required for background notifications." }, statusCode: 403);
    private static async ValueTask<object?> SerializeWrites(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method)) return await next(context);
        // This SQLite release runs one app instance. Serialize count-and-write plan-limit checks.
        var gate = context.HttpContext.RequestServices.GetRequiredService<ProductAlertWriteLock>().Gate;
        await gate.WaitAsync(context.HttpContext.RequestAborted);
        try { return await next(context); }
        finally { gate.Release(); }
    }
    public static string DetailUrl(Guid id) => $"/app?tab=alerts&alert={id}";
    private static object RuleView(CustomerAlertRule x) => new { x.Id, x.Name, x.Enabled, symbols = AlertJson.Strings(x.SymbolsJson), setupTypes = AlertJson.Strings(x.SetupTypesJson), x.MinimumScore, x.HoldSeconds, x.CooldownMinutes, x.CreatedAt };
    private static object HistoryView(CustomerAlertRecord x, IEnumerable<CustomerPushDelivery> deliveries, CustomerAlertOutcome? outcome, DateTimeOffset now) => new
    {
        x.Id, x.RuleId, x.RuleName, x.Symbol, x.SetupType, x.IssuedAt, x.ExpiresAt,
        status = x.ExpiresAt <= now ? "expired" : x.Status, issueStatus = x.Status, x.Message, x.IsTest,
        detailUrl = DetailUrl(x.Id), evidence = JsonSerializer.Deserialize<JsonElement>(x.EvidenceJson),
        deliveries = deliveries.Select(d => new { d.DeviceId, d.State, d.Attempts, d.LastAttemptAt, d.LastError }),
        outcome = outcome is null ? null : new { outcome.Status, outcome.InitialPrice, outcome.TargetPrice, outcome.StopPrice, outcome.WindowEndsAt, outcome.ObservedAt, outcome.ObservedPrice, outcome.DataGap, interpretation = CustomerAlertOutcome.Interpretation },
    };

    private static async Task<IResult> SaveRule(Guid? id, AlertRuleRequest request, ClaimsPrincipal user, ProductDbContext db, ProductOptions options, ScannerOptions scanner, TimeProvider time, CancellationToken ct)
    {
        var userId = ProductAccess.UserId(user)!;
        var floor = Math.Max(scanner.SetupScoreThreshold, scanner.Execution.MinScore);
        var symbols = (request.Symbols ?? []).Select(x => (x ?? "").Trim().ToUpperInvariant()).Distinct().ToArray();
        var types = (request.SetupTypes ?? []).Select(x => x ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || symbols.Length > options.Limits.ProWatchlist
            || symbols.Any(x => x.Length > 30 || !System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Z0-9]+-USD$"))
            || types.Any(x => !Enum.TryParse<SetupType>(x, true, out var t) || !Enum.IsDefined(t) || t == SetupType.None)
            || !double.IsFinite(request.MinimumScore) || request.MinimumScore < floor || request.MinimumScore > 100
            || request.HoldSeconds is < 0 or > 3600 || request.CooldownMinutes is < 5 or > 10080)
            return Results.BadRequest(new { error = $"Enter a name, supported USD spot symbols, setup types, score {floor}–100, hold 0–3600 seconds, and cooldown 5–10080 minutes." });
        if (id is null && await db.AlertRules.CountAsync(x => x.UserId == userId, ct) >= options.Limits.ProAlertRules)
            return Results.BadRequest(new { error = "Your plan's alert-rule limit has been reached." });
        var row = id is { } key ? await db.AlertRules.SingleOrDefaultAsync(x => x.Id == key && x.UserId == userId, ct) : null;
        if (id.HasValue && row is null) return Results.NotFound();
        row ??= new CustomerAlertRule { UserId = userId, CreatedAt = time.GetUtcNow() };
        row.Name = request.Name.Trim(); row.Enabled = request.Enabled; row.SymbolsJson = AlertJson.Serialize(symbols);
        row.SetupTypesJson = AlertJson.Serialize(types.Select(x => Enum.Parse<SetupType>(x, true).ToString()));
        row.MinimumScore = request.MinimumScore; row.HoldSeconds = request.HoldSeconds; row.CooldownMinutes = request.CooldownMinutes;
        if (!id.HasValue) db.AlertRules.Add(row);
        // Retain cooldowns on edits: changing a label or toggling a rule must not bypass deduplication.
        await db.SaveChangesAsync(ct);
        return Results.Ok(RuleView(row));
    }
}

public sealed class ProductAlertWriteLock : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public void Dispose() => Gate.Dispose();
}

public static class PushEndpointPolicy
{
    public static bool IsAllowed(string? endpoint)
    {
        if (endpoint is null || endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        var host = uri.IdnHost;
        return host.Equals("fcm.googleapis.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("updates.push.services.mozilla.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("push.services.mozilla.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("web.push.apple.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ValidKeys(PushKeys keys)
    {
        try
        {
            static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
            if (string.IsNullOrWhiteSpace(keys.P256dh) || keys.P256dh.Length > 100 || string.IsNullOrWhiteSpace(keys.Auth) || keys.Auth.Length > 30) return false;
            var point = Decode(keys.P256dh);
            if (point.Length != 65 || point[0] != 4 || Decode(keys.Auth).Length != 16) return false;
            using var curve = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } });
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }
}
