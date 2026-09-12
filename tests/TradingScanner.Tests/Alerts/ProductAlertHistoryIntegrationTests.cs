using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingScanner.Api.Product;
using TradingScanner.Tests.Api;
using Xunit;

namespace TradingScanner.Tests.Alerts;

public sealed class ProductAlertHistoryIntegrationTests
{
    private sealed class Factory : ApiIntegrationTests.Factory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Product:CommercialMode", "true");
            builder.UseSetting("Product:MarketDataApproved", "false");
            builder.UseSetting("Product:Billing:PriceId", "price_history_fixture");
            builder.UseSetting("Product:Limits:AlertHistoryDays", "30");
            builder.UseSetting("Product:Mail:Host", "");
            builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
        }
    }

    [Fact]
    public async Task History_listing_and_deep_links_both_enforce_customer_ownership_and_configured_window()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var password = "HistoryTestPassword42";
        var now = DateTimeOffset.UtcNow;
        var visibleId = Guid.NewGuid(); var oldId = Guid.NewGuid(); var foreignId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ProductUser>>();
            var alice = new ProductUser { UserName = "history-alice@example.test", Email = "history-alice@example.test" };
            var bob = new ProductUser { UserName = "history-bob@example.test", Email = "history-bob@example.test" };
            Assert.True((await users.CreateAsync(alice, password)).Succeeded);
            Assert.True((await users.CreateAsync(bob, password)).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Preferences.Add(new CustomerPreferences { UserId = alice.Id });
            db.Subscriptions.Add(new ProductSubscription { UserId = alice.Id, Status = "active", PriceId = "price_history_fixture", StripeSubscriptionId = "sub_history_fixture", LatestInvoicePaid = true, CurrentPeriodEnd = now.AddDays(1) });
            db.AlertRecords.AddRange(
                new CustomerAlertRecord { Id = visibleId, UserId = alice.Id, RuleName = "Within my window", IssuedAt = now.AddDays(-1), ExpiresAt = now.AddDays(-1).AddMinutes(5) },
                new CustomerAlertRecord { Id = oldId, UserId = alice.Id, RuleName = "Outside my window", IssuedAt = now.AddDays(-31), ExpiresAt = now.AddDays(-31).AddMinutes(5) },
                new CustomerAlertRecord { Id = foreignId, UserId = bob.Id, RuleName = "Another customer", IssuedAt = now.AddHours(-1), ExpiresAt = now.AddMinutes(-55) });
            await db.SaveChangesAsync();
        }
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/product/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/product/auth/login") { Content = JsonContent.Create(new { email = "history-alice@example.test", password }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var listing = await client.GetFromJsonAsync<JsonElement>("/api/product/alerts/history");
        Assert.Equal(1, listing.GetArrayLength()); Assert.Equal(visibleId, listing[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/product/alerts/history/{visibleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/product/alerts/history/{oldId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/product/alerts/history/{foreignId}")).StatusCode);
        await using var verify = factory.Services.CreateAsyncScope();
        Assert.Equal(3, await verify.ServiceProvider.GetRequiredService<ProductDbContext>().AlertRecords.CountAsync());
    }
}
