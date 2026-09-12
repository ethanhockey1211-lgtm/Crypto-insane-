using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.Product;

public sealed class BillingOptions
{
    public bool Enabled { get; set; }
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string PriceId { get; set; } = "";
    public bool Configured => Enabled && SecretKey.StartsWith("sk_test_", StringComparison.Ordinal)
        && WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal) && PriceId.StartsWith("price_", StringComparison.Ordinal);
}

public sealed class BillingException(string message) : Exception(message);
public sealed class BillingMutex { public SemaphoreSlim Gate { get; } = new(1, 1); }
public sealed record StripeSubscriptionSnapshot(string Id, string CustomerId, string PriceId, string Status,
    DateTimeOffset? PeriodEnd, bool CancelAtPeriodEnd, bool LatestInvoicePaid, bool LiveMode);
public sealed record StripeCheckout(string Url, DateTimeOffset ExpiresAt);

public interface IStripeGateway
{
    Task<string> CreateCustomerAsync(string userId, string email, CancellationToken ct);
    Task<StripeCheckout> CreateCheckoutAsync(string userId, string customerId, string attemptId, DateTimeOffset expiresAt, CancellationToken ct);
    Task<string> CreatePortalAsync(string customerId, CancellationToken ct);
    Task<StripeSubscriptionSnapshot> GetSubscriptionAsync(string subscriptionId, CancellationToken ct);
    Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct);
    Task DeleteCustomerAsync(string customerId, CancellationToken ct);
}

/// <summary>Hosted Stripe test checkout only. Live keys and live API objects are rejected in code.</summary>
public sealed class StripeGateway(HttpClient http, IOptions<BillingOptions> options, IOptions<ProductOptions> product) : IStripeGateway
{
    public async Task<string> CreateCustomerAsync(string userId, string email, CancellationToken ct)
    {
        using var doc = await RequestAsync(HttpMethod.Post, "customers", new() { ["email"] = email, ["metadata[scanner_user_id]"] = userId }, "scanner-customer-" + userId, ct);
        AssertTest(doc.RootElement);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<StripeCheckout> CreateCheckoutAsync(string userId, string customerId, string attemptId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // Validate the configured server-side price. The client never supplies a price, amount or customer ID.
        using var price = await RequestAsync(HttpMethod.Get, "prices/" + Uri.EscapeDataString(options.Value.PriceId), null, null, ct);
        AssertTest(price.RootElement);
        var p = price.RootElement;
        if (p.GetProperty("currency").GetString() != "usd" || p.GetProperty("unit_amount").GetInt64() != 2900
            || !p.GetProperty("active").GetBoolean() || p.GetProperty("recurring").GetProperty("interval").GetString() != "month"
            || p.GetProperty("recurring").GetProperty("interval_count").GetInt32() != 1)
            throw new BillingException("The test Pro price must be an active USD $29 monthly recurring price.");
        var origin = product.Value.PublicOrigin.TrimEnd('/');
        using var doc = await RequestAsync(HttpMethod.Post, "checkout/sessions", new()
        {
            ["mode"] = "subscription", ["customer"] = customerId, ["line_items[0][price]"] = options.Value.PriceId, ["line_items[0][quantity]"] = "1",
            ["client_reference_id"] = userId, ["subscription_data[metadata][scanner_user_id]"] = userId,
            ["success_url"] = origin + "/app?tab=account&checkout=success", ["cancel_url"] = origin + "/app?tab=account&checkout=cancelled",
            ["allow_promotion_codes"] = "false",
            ["expires_at"] = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        }, "scanner-checkout-" + attemptId, ct);
        AssertTest(doc.RootElement);
        var url = doc.RootElement.GetProperty("url").GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "checkout.stripe.com") throw new BillingException("Stripe returned an unexpected checkout destination.");
        return new(url, DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("expires_at").GetInt64()));
    }

    public async Task<string> CreatePortalAsync(string customerId, CancellationToken ct)
    {
        using var doc = await RequestAsync(HttpMethod.Post, "billing_portal/sessions", new() { ["customer"] = customerId, ["return_url"] = product.Value.PublicOrigin.TrimEnd('/') + "/app?tab=account" }, null, ct);
        var url = doc.RootElement.GetProperty("url").GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "billing.stripe.com") throw new BillingException("Stripe returned an unexpected portal destination.");
        return url;
    }

    public async Task<StripeSubscriptionSnapshot> GetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        using var doc = await RequestAsync(HttpMethod.Get, "subscriptions/" + Uri.EscapeDataString(subscriptionId) + "?expand[]=latest_invoice", null, null, ct);
        var s = doc.RootElement;
        AssertTest(s);
        var items = s.GetProperty("items").GetProperty("data");
        var priceId = items.GetArrayLength() == 1 ? items[0].GetProperty("price").GetProperty("id").GetString()! : "";
        // API is pinned to Acacia; the item fallback also accepts newer snapshot representations.
        var end = s.TryGetProperty("current_period_end", out var period) ? period.GetInt64()
            : items.GetArrayLength() == 1 && items[0].TryGetProperty("current_period_end", out period) ? period.GetInt64() : 0;
        var paid = s.TryGetProperty("latest_invoice", out var invoice) && invoice.ValueKind == JsonValueKind.Object
            && invoice.TryGetProperty("paid", out var paidFlag) && paidFlag.GetBoolean();
        return new(s.GetProperty("id").GetString()!, JsonId(s.GetProperty("customer"))!, priceId,
            s.GetProperty("status").GetString()!, end > 0 ? DateTimeOffset.FromUnixTimeSeconds(end) : null,
            s.GetProperty("cancel_at_period_end").GetBoolean(), paid, false);
    }

    public async Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        using var doc = await RequestAsync(HttpMethod.Delete, "subscriptions/" + Uri.EscapeDataString(subscriptionId), null, null, ct);
        AssertTest(doc.RootElement);
        if (doc.RootElement.GetProperty("status").GetString() != "canceled") throw new BillingException("Subscription cancellation has not been confirmed. Try again before deleting your account.");
    }

    public async Task DeleteCustomerAsync(string customerId, CancellationToken ct)
    {
        var path = "customers/" + Uri.EscapeDataString(customerId);
        using var current = await RequestAsync(HttpMethod.Get, path, null, null, ct);
        if (current.RootElement.TryGetProperty("deleted", out var deleted) && deleted.GetBoolean()) return;
        AssertTest(current.RootElement);
        using var result = await RequestAsync(HttpMethod.Delete, path, null, null, ct);
        if (!result.RootElement.TryGetProperty("deleted", out deleted) || !deleted.GetBoolean())
            throw new BillingException("Payment account deletion has not been confirmed. Retry before deleting your scanner account.");
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, Dictionary<string, string>? form, string? idempotencyKey, CancellationToken ct)
    {
        if (!options.Value.Configured) throw new BillingException("Stripe test billing is not configured. Live charging is disabled.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
        request.Headers.Add("Stripe-Version", "2025-02-24.acacia");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new BillingException("The payment provider could not complete this test request. Retry or contact support.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException) { throw new BillingException("The payment provider is unavailable. Please retry."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BillingException("The payment provider timed out. Please retry."); }
    }

    private static void AssertTest(JsonElement value)
    {
        if (!value.TryGetProperty("livemode", out var live) || live.ValueKind != JsonValueKind.False) throw new BillingException("Only Stripe test-mode resources are supported. Live charging is disabled.");
    }
    internal static string? JsonId(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) ? id.GetString() : null;
}

public sealed class BillingService(ProductDbContext db, IStripeGateway stripe, IOptions<BillingOptions> options, BillingMutex mutex, TimeProvider time)
{
    public async Task<string> CheckoutAsync(ProductUser user, CancellationToken ct)
    {
        if (!options.Value.Configured) throw new BillingException("Stripe test billing is not configured. Live charging is disabled.");
        if (!user.EmailConfirmed) throw new BillingException("Confirm your email address before opening test checkout.");
        await mutex.Gate.WaitAsync(ct);
        try
        {
            var s = await db.Subscriptions.SingleAsync(x => x.UserId == user.Id, ct);
            if (s.StripeSubscriptionId is not null && s.Status is not ("canceled" or "incomplete_expired" or "free"))
                throw new BillingException("A subscription already exists. Use subscription management to change or cancel it.");
            if (s.CheckoutSessionUrl is not null && s.CheckoutExpiresAt > time.GetUtcNow()) return s.CheckoutSessionUrl;
            if (s.StripeCustomerId is null)
            {
                s.StripeCustomerId = await stripe.CreateCustomerAsync(user.Id, user.Email!, ct);
                await db.SaveChangesAsync(ct);
            }
            if (s.CheckoutAttemptId is null || s.CheckoutExpiresAt <= time.GetUtcNow())
            {
                s.CheckoutAttemptId = Guid.NewGuid().ToString("N");
                s.CheckoutExpiresAt = time.GetUtcNow().AddHours(1);
                // Persist before calling the provider: a timeout or process restart reuses this attempt.
                await db.SaveChangesAsync(ct);
            }
            var checkout = await stripe.CreateCheckoutAsync(user.Id, s.StripeCustomerId, s.CheckoutAttemptId, s.CheckoutExpiresAt!.Value, ct);
            s.CheckoutSessionUrl = checkout.Url; s.CheckoutExpiresAt = checkout.ExpiresAt;
            await db.SaveChangesAsync(ct);
            return checkout.Url;
        }
        finally { mutex.Gate.Release(); }
    }

    public async Task<string> PortalAsync(string userId, CancellationToken ct)
    {
        var s = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
        if (s.StripeCustomerId is null) throw new BillingException("No test billing account exists yet.");
        return await stripe.CreatePortalAsync(s.StripeCustomerId, ct);
    }

    public async Task CancelForDeletionAsync(string userId, CancellationToken ct)
    {
        await mutex.Gate.WaitAsync(ct);
        try
        {
            var s = await db.Subscriptions.SingleAsync(x => x.UserId == userId, ct);
            if (s.StripeCustomerId is not null)
            {
                // Stripe customer deletion cancels ALL subscriptions and prevents new ones, including
                // a checkout that completed before its webhook was delivered. Retry reads tombstones.
                await stripe.DeleteCustomerAsync(s.StripeCustomerId, ct);
                s.Status = "canceled"; s.LatestInvoicePaid = false; s.CurrentPeriodEnd = time.GetUtcNow();
                s.CheckoutAttemptId = null; s.CheckoutExpiresAt = null; s.CheckoutSessionUrl = null;
                await db.SaveChangesAsync(ct);
            }
        }
        finally { mutex.Gate.Release(); }
    }

    public async Task<bool> ProcessWebhookAsync(JsonElement evt, CancellationToken ct)
    {
        if (evt.GetProperty("livemode").GetBoolean()) throw new BillingException("Live billing events are disabled.");
        var eventId = evt.GetProperty("id").GetString()!;
        var type = evt.GetProperty("type").GetString()!;
        if (!eventId.StartsWith("evt_", StringComparison.Ordinal) || eventId.Length > 255) throw new BillingException("Invalid event identifier.");
        await mutex.Gate.WaitAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.BillingEvents.AnyAsync(x => x.Id == eventId, ct)) return false;
            var obj = evt.GetProperty("data").GetProperty("object");
            string? subscriptionId = null;
            if (type.StartsWith("customer.subscription.", StringComparison.Ordinal)) subscriptionId = obj.GetProperty("id").GetString();
            else if (type is "checkout.session.completed" or "checkout.session.async_payment_succeeded" or "checkout.session.async_payment_failed"
                or "invoice.paid" or "invoice.payment_failed" or "invoice.payment_action_required" or "invoice.finalization_failed")
            {
                if (obj.TryGetProperty("subscription", out var subscription)) subscriptionId = StripeGateway.JsonId(subscription);
                else if (obj.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object
                    && parent.TryGetProperty("subscription_details", out var detail) && detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("subscription", out subscription)) subscriptionId = StripeGateway.JsonId(subscription);
            }
            if (subscriptionId is not null)
            {
                // Retrieve current provider state so retries and out-of-order snapshots cannot resurrect an
                // old active subscription after failure/cancellation. Never trust checkout redirects.
                var latest = await stripe.GetSubscriptionAsync(subscriptionId, ct);
                if (latest.LiveMode) throw new BillingException("Live billing events are disabled.");
                var local = await db.Subscriptions.SingleOrDefaultAsync(x => x.StripeCustomerId == latest.CustomerId, ct);
                if (local is not null && (local.StripeSubscriptionId is null || local.StripeSubscriptionId == latest.Id || local.Status is "canceled" or "incomplete_expired"))
                {
                    local.StripeSubscriptionId = latest.Id; local.PriceId = latest.PriceId; local.Status = latest.Status;
                    local.CurrentPeriodEnd = latest.PeriodEnd; local.CancelAtPeriodEnd = latest.CancelAtPeriodEnd;
                    local.LatestInvoicePaid = latest.LatestInvoicePaid; local.LastVerifiedAt = time.GetUtcNow();
                    local.CheckoutExpiresAt = null; local.CheckoutSessionUrl = null; local.CheckoutAttemptId = null;
                }
            }
            db.BillingEvents.Add(new BillingEvent { Id = eventId, Type = type, ProcessedAt = time.GetUtcNow() });
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return true;
        }
        finally { mutex.Gate.Release(); }
    }
}

public static class StripeSignature
{
    public static bool Verify(string payload, string? header, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header) || !secret.StartsWith("whsec_", StringComparison.Ordinal)) return false;
        var fields = header.Split(',').Select(x => x.Trim().Split('=', 2)).Where(x => x.Length == 2).ToArray();
        if (!long.TryParse(fields.FirstOrDefault(x => x[0] == "t")?[1], NumberStyles.None, CultureInfo.InvariantCulture, out var stamp)
            || Math.Abs((decimal)now.ToUnixTimeSeconds() - stamp) > 300) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(stamp.ToString(CultureInfo.InvariantCulture) + "." + payload));
        foreach (var value in fields.Where(x => x[0] == "v1"))
        {
            try { if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(value[1]))) return true; }
            catch (FormatException) { }
        }
        return false;
    }
}

public static class ProductBillingEndpoints
{
    public static IEndpointRouteBuilder MapProductBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product/billing").RequireRateLimiting("api");
        group.MapPost("/checkout", async (HttpContext ctx, UserManager<ProductUser> users, BillingService billing) =>
        {
            try { return Results.Ok(new { url = await billing.CheckoutAsync((await users.GetUserAsync(ctx.User))!, ctx.RequestAborted) }); }
            catch (BillingException ex) { return ProductAccountEndpoints.Error(ex.Message, 503); }
        }).RequireAuthorization();
        group.MapPost("/portal", async (HttpContext ctx, BillingService billing) =>
        {
            try { return Results.Ok(new { url = await billing.PortalAsync(ProductAccess.UserId(ctx.User)!, ctx.RequestAborted) }); }
            catch (BillingException ex) { return ProductAccountEndpoints.Error(ex.Message, 503); }
        }).RequireAuthorization();
        group.MapPost("/webhook", async (HttpContext ctx, IOptions<BillingOptions> options, BillingService billing, TimeProvider time) =>
        {
            if (!options.Value.Configured) return ProductAccountEndpoints.Error("Test billing is not configured.", 503);
            if (ctx.Request.ContentLength > 1024 * 1024) return Results.StatusCode(413);
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var buffer = new char[1024 * 1024 + 1];
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), ctx.RequestAborted);
            if (length > 1024 * 1024) return Results.StatusCode(413);
            var body = new string(buffer, 0, length);
            if (!StripeSignature.Verify(body, ctx.Request.Headers["Stripe-Signature"], options.Value.WebhookSecret, time.GetUtcNow()))
                return ProductAccountEndpoints.Error("Invalid webhook signature.", 400);
            try
            {
                using var evt = JsonDocument.Parse(body);
                if (evt.RootElement.GetProperty("livemode").GetBoolean()) return ProductAccountEndpoints.Error("Live billing events are disabled.", 400);
                var processed = await billing.ProcessWebhookAsync(evt.RootElement, ctx.RequestAborted);
                return Results.Ok(new { received = true, duplicate = !processed });
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return ProductAccountEndpoints.Error("Invalid webhook payload.", 400); }
            catch (BillingException ex) { return ProductAccountEndpoints.Error(ex.Message, 503); }
        });
        return app;
    }
}
