using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingScanner.Api.Product;
using Xunit;

namespace TradingScanner.Tests.Api;

public sealed class ProductAccountIntegrationTests
{
    private const string Password = "CorrectHorseBattery42";
    private const string WebhookSecret = "whsec_explicit_test_fixture_not_a_credential";

    private sealed class FakeStripe : IStripeGateway
    {
        public int CheckoutCalls { get; private set; }
        public int SubscriptionReads { get; private set; }
        public bool FailCheckoutOnce { get; set; }
        public bool CustomerDeleted { get; private set; }
        public List<string> CheckoutAttempts { get; } = [];
        public StripeSubscriptionSnapshot Latest { get; set; } = new("sub_fixture", "cus_fixture", "price_fixture", "active", DateTimeOffset.UtcNow.AddDays(30), false, true, false);
        public Task<string> CreateCustomerAsync(string userId, string email, CancellationToken ct) => Task.FromResult("cus_fixture");
        public Task<StripeCheckout> CreateCheckoutAsync(string userId, string customerId, string attemptId, DateTimeOffset expiresAt, CancellationToken ct)
        {
            CheckoutCalls++; CheckoutAttempts.Add(attemptId);
            if (FailCheckoutOnce) { FailCheckoutOnce = false; throw new BillingException("Simulated uncertain provider timeout."); }
            return Task.FromResult(new StripeCheckout("https://checkout.stripe.com/c/test_fixture", expiresAt));
        }
        public Task<string> CreatePortalAsync(string customerId, CancellationToken ct) => Task.FromResult("https://billing.stripe.com/p/session/test_fixture");
        public Task<StripeSubscriptionSnapshot> GetSubscriptionAsync(string subscriptionId, CancellationToken ct) { SubscriptionReads++; return Task.FromResult(Latest); }
        public Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct) { Latest = Latest with { Status = "canceled", LatestInvoicePaid = false }; return Task.CompletedTask; }
        public Task DeleteCustomerAsync(string customerId, CancellationToken ct) { CustomerDeleted = true; Latest = Latest with { Status = "canceled", LatestInvoicePaid = false }; return Task.CompletedTask; }
    }

    private sealed class Factory : ApiIntegrationTests.Factory
    {
        public FakeStripe Stripe { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Product:CommercialMode", "true");
            builder.UseSetting("Product:MarketDataApproved", "false");
            builder.UseSetting("Product:Billing:Enabled", "true");
            builder.UseSetting("Product:Billing:SecretKey", "sk_test_explicit_fixture_not_a_credential");
            builder.UseSetting("Product:Billing:WebhookSecret", WebhookSecret);
            builder.UseSetting("Product:Billing:PriceId", "price_fixture");
            builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
            builder.ConfigureTestServices(services => { services.RemoveAll<IStripeGateway>(); services.AddSingleton<IStripeGateway>(Stripe); });
        }
    }

    private static async Task<HttpResponseMessage> Write(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/product/auth/csrf");
        using var request = new HttpRequestMessage(method, "/api/product/" + path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task Register(HttpClient client, string email)
    {
        var response = await Write(client, HttpMethod.Post, "auth/register", new { email, password = Password, displayName = "Test customer" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task Confirm(Factory factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ProductUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        Assert.True((await users.ConfirmEmailAsync(user, await users.GenerateEmailConfirmationTokenAsync(user))).Succeeded);
    }

    private static Task<JsonElement> Me(HttpClient client) => client.GetFromJsonAsync<JsonElement>("/api/product/me");
    private static bool IsPro(JsonElement me) => me.GetProperty("entitlements").GetProperty("scanner").GetBoolean();

    [Fact]
    public async Task Accounts_require_csrf_hash_passwords_and_keep_watchlists_private_across_sessions()
    {
        using var factory = new Factory();
        using var alice = factory.CreateClient();
        using var bob = factory.CreateClient();
        Assert.False((await Me(alice)).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/product/auth/register", new { email = "alice@example.test", password = Password })).StatusCode);
        await Register(alice, "alice@example.test");
        await Register(bob, "bob@example.test");
        Assert.Equal(HttpStatusCode.OK, (await Write(alice, HttpMethod.Put, "watchlist", new { symbols = new[] { "BTC-USD", "ETH-USD" }, userId = "someone-else" })).StatusCode);
        Assert.Empty((await Me(bob)).GetProperty("watchlist").EnumerateArray());
        Assert.Equal(2, (await Me(alice)).GetProperty("watchlist").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await Write(alice, HttpMethod.Post, "support/", new { subject = "Device setup", message = "Please help with device setup." })).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>("/api/product/support/")).EnumerateArray());
        Assert.Single((await alice.GetFromJsonAsync<JsonElement>("/api/product/support/")).EnumerateArray());
        using var secondDevice = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Write(secondDevice, HttpMethod.Post, "auth/login", new { email = "alice@example.test", password = Password })).StatusCode);
        Assert.Equal(2, (await Me(secondDevice)).GetProperty("watchlist").GetArrayLength());
        await using var scope = factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ProductUser>>().FindByEmailAsync("alice@example.test");
        Assert.NotNull(user!.PasswordHash);
        Assert.DoesNotContain(Password, user.PasswordHash);
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<ProductDbContext>().Users.CountAsync());
        Assert.Equal(HttpStatusCode.OK, (await Write(alice, HttpMethod.Post, "auth/logout")).StatusCode);
        Assert.False((await Me(alice)).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Onboarding_validates_scope_quiet_hours_costs_and_free_watchlist_limits()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        await Register(client, "onboarding@example.test");
        var preferences = new PreferencesRequest("kraken", "spot-usd", ["breakout", "trend"], true, true, "22:30", "07:15", "UTC", 16m, 26m, 12m, true);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Put, "preferences", preferences)).StatusCode);
        var me = await Me(client);
        Assert.Equal(26m, me.GetProperty("preferences").GetProperty("takerFeeBps").GetDecimal());
        Assert.True(me.GetProperty("preferences").GetProperty("onboardingComplete").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Put, "preferences", preferences with { Exchange = "coinbase" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Put, "preferences", preferences with { QuietHoursStart = "29:99" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Put, "preferences", preferences with { TakerFeeBps = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Put, "watchlist", new { symbols = new[] { "BTC-USD", "ETH-USD", "SOL-USD", "ADA-USD", "XRP-USD", "LTC-USD" } })).StatusCode);
    }

    [Fact]
    public async Task Password_recovery_is_bound_to_the_account_and_revokes_existing_sessions()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        await Register(client, "recovery@example.test");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Write(client, HttpMethod.Post, "auth/forgot-password", new { email = "recovery@example.test" })).StatusCode);
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ProductUser>>();
            token = await users.GeneratePasswordResetTokenAsync((await users.FindByEmailAsync("recovery@example.test"))!);
        }
        var request = new { email = "recovery@example.test", token, password = "AnotherStrongPassword42" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Post, "auth/reset-password", new { email = "wrong@example.test", token, password = "AnotherStrongPassword42" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "auth/reset-password", request)).StatusCode);
        Assert.False((await Me(client)).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Post, "auth/reset-password", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, HttpMethod.Post, "auth/login", new { email = "recovery@example.test", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "auth/login", new { email = "recovery@example.test", password = "AnotherStrongPassword42" })).StatusCode);
    }

    [Fact]
    public async Task Checkout_reuses_uncertain_attempt_and_redirect_cannot_grant_pro()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        await Register(client, "checkout@example.test");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Write(client, HttpMethod.Post, "billing/checkout")).StatusCode);
        await Confirm(factory, "checkout@example.test");
        factory.Stripe.FailCheckoutOnce = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Write(client, HttpMethod.Post, "billing/checkout")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "billing/checkout")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "billing/checkout")).StatusCode);
        Assert.Equal(2, factory.Stripe.CheckoutCalls);
        Assert.Single(factory.Stripe.CheckoutAttempts.Distinct());
        Assert.False(IsPro(await client.GetFromJsonAsync<JsonElement>("/api/product/me?checkout=success")));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/scanner/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "billing/portal")).StatusCode);
    }

    [Fact]
    public async Task Signed_webhooks_are_idempotent_reconcile_current_state_and_revoke_on_failure_cancel_and_expiry()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        await Register(client, "webhooks@example.test"); await Confirm(factory, "webhooks@example.test");
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "billing/checkout")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(client, "evt_invalid", "customer.subscription.updated", badSignature: true)).StatusCode);
        Assert.False(IsPro(await Me(client)));
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(client, "evt_live", "customer.subscription.updated", live: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(client, "evt_paid", "invoice.paid")).StatusCode);
        Assert.True(IsPro(await Me(client)));
        var duplicate = await Webhook(client, "evt_paid", "invoice.paid");
        Assert.True((await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, factory.Stripe.SubscriptionReads);
        // Even a valid old 'paid' event must retrieve current delinquent state, never replay paid access.
        factory.Stripe.Latest = factory.Stripe.Latest with { Status = "past_due", LatestInvoicePaid = false };
        Assert.Equal(HttpStatusCode.OK, (await Webhook(client, "evt_old_paid_arrives_late", "invoice.paid")).StatusCode);
        Assert.False(IsPro(await Me(client)));
        factory.Stripe.Latest = factory.Stripe.Latest with { Status = "active", LatestInvoicePaid = true, CancelAtPeriodEnd = true };
        await Webhook(client, "evt_cancel_scheduled", "customer.subscription.updated");
        Assert.True(IsPro(await Me(client)));
        factory.Stripe.Latest = factory.Stripe.Latest with { PeriodEnd = DateTimeOffset.UtcNow.AddSeconds(-1) };
        await Webhook(client, "evt_expired", "customer.subscription.updated");
        Assert.False(IsPro(await Me(client)));
        factory.Stripe.Latest = factory.Stripe.Latest with { Status = "canceled", PeriodEnd = DateTimeOffset.UtcNow.AddDays(10) };
        await Webhook(client, "evt_cancelled", "customer.subscription.deleted");
        Assert.False(IsPro(await Me(client)));
    }

    [Fact]
    public async Task Commercial_guard_blocks_legacy_shared_stores_and_paid_access_still_requires_data_permission()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/market/symbols")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/hubs/market/negotiate?negotiateVersion=1", null)).StatusCode);
        await Register(client, "guard@example.test"); await Confirm(factory, "guard@example.test");
        await Write(client, HttpMethod.Post, "billing/checkout"); await Webhook(client, "evt_guard", "invoice.paid");
        Assert.True(IsPro(await Me(client)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/market/symbols")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/product/scanner")).StatusCode);
    }

    [Fact]
    public async Task Account_deletion_reauthenticates_cancels_subscription_and_cascades_customer_data()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        await Register(client, "delete@example.test"); await Confirm(factory, "delete@example.test");
        await Write(client, HttpMethod.Put, "watchlist", new { symbols = new[] { "BTC-USD" } });
        await Write(client, HttpMethod.Post, "support/", new { subject = "Deletion", message = "Delete my saved profile." });
        await Write(client, HttpMethod.Post, "billing/checkout"); await Webhook(client, "evt_delete", "invoice.paid");
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Delete, "account", new { password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Delete, "account", new { password = Password })).StatusCode);
        Assert.Equal("canceled", factory.Stripe.Latest.Status);
        Assert.True(factory.Stripe.CustomerDeleted);
        Assert.False((await Me(client)).GetProperty("authenticated").GetBoolean());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.Watchlist.ToListAsync());
        Assert.Empty(await db.Preferences.ToListAsync()); Assert.Empty(await db.Subscriptions.ToListAsync());
        Assert.Empty(await db.SupportRequests.ToListAsync());
    }

    [Fact]
    public async Task Deletion_closes_provider_customer_even_when_checkout_has_no_subscription_webhook_yet()
    {
        using var factory = new Factory(); using var client = factory.CreateClient();
        await Register(client, "pending-checkout@example.test"); await Confirm(factory, "pending-checkout@example.test");
        await Write(client, HttpMethod.Post, "billing/checkout");
        Assert.False(IsPro(await Me(client)));
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Delete, "account", new { password = Password })).StatusCode);
        Assert.True(factory.Stripe.CustomerDeleted);
    }

    private static async Task<HttpResponseMessage> Webhook(HttpClient client, string id, string type, bool badSignature = false, bool live = false)
    {
        var body = JsonSerializer.Serialize(new { id, type, livemode = live, created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data = new { @object = new { id = "sub_fixture", subscription = "sub_fixture" } } });
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{stamp}.{body}"))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/product/billing/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", $"t={stamp},v1={(badSignature ? new string('0', 64) : signature)}");
        return await client.SendAsync(request);
    }

    [Fact]
    public void Signature_rejects_tampering_expired_delivery_and_live_configuration()
    {
        var now = DateTimeOffset.UtcNow; var body = "{\"test\":true}"; var stamp = now.ToUnixTimeSeconds();
        var signed = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{stamp}.{body}")));
        Assert.True(StripeSignature.Verify(body, $"t={stamp},v1={signed}", WebhookSecret, now));
        Assert.False(StripeSignature.Verify(body + " ", $"t={stamp},v1={signed}", WebhookSecret, now));
        Assert.False(StripeSignature.Verify(body, $"t={stamp},v1={signed}", WebhookSecret, now.AddMinutes(6)));
        Assert.False(new BillingOptions { Enabled = true, SecretKey = "sk_live_fixture_rejected", PriceId = "price_fixture", WebhookSecret = WebhookSecret }.Configured);
    }
}
