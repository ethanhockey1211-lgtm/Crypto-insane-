using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.Product;

public sealed class ProductOptions
{
    public bool CommercialMode { get; set; } = true;
    public bool MarketDataApproved { get; set; }
    public string PublicOrigin { get; set; } = "http://localhost:5080";
    public string DatabasePath { get; set; } = "data/product.db";
    public string DataProtectionKeysPath { get; set; } = "data/keys";
    public PlanLimits Limits { get; set; } = new();
}

public sealed class PlanLimits
{
    public int FreeWatchlist { get; set; } = 5;
    public int ProWatchlist { get; set; } = 100;
    public int ProAlertRules { get; set; } = 20;
    public int AlertHistoryDays { get; set; } = 90;
}

public sealed class ProductUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CustomerPreferences
{
    public string UserId { get; set; } = "";
    public string Exchange { get; set; } = "kraken";
    public string MarketScope { get; set; } = "spot-usd";
    public string ConditionsJson { get; set; } = "[\"breakout\",\"trend\"]";
    public bool PushEnabled { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public string QuietHoursStart { get; set; } = "22:00";
    public string QuietHoursEnd { get; set; } = "08:00";
    public string TimeZone { get; set; } = "UTC";
    public decimal MakerFeeBps { get; set; } = 25m;
    public decimal TakerFeeBps { get; set; } = 40m;
    public decimal SlippageBps { get; set; } = 10m;
    public bool OnboardingComplete { get; set; }
}

public sealed class WatchlistItem
{
    public string UserId { get; set; } = "";
    public string Symbol { get; set; } = "";
}

public sealed class ProductSubscription
{
    public string UserId { get; set; } = "";
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? PriceId { get; set; }
    public string Status { get; set; } = "free";
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public bool LatestInvoicePaid { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? CheckoutSessionUrl { get; set; }
    public string? CheckoutAttemptId { get; set; }
    public DateTimeOffset? CheckoutExpiresAt { get; set; }
}

public sealed class BillingEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

public partial class ProductDbContext(DbContextOptions<ProductDbContext> options) : IdentityDbContext<ProductUser>(options)
{
    public DbSet<CustomerPreferences> Preferences => Set<CustomerPreferences>();
    public DbSet<WatchlistItem> Watchlist => Set<WatchlistItem>();
    public DbSet<ProductSubscription> Subscriptions => Set<ProductSubscription>();
    public DbSet<BillingEvent> BillingEvents => Set<BillingEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<CustomerPreferences>().HasKey(x => x.UserId);
        builder.Entity<CustomerPreferences>().HasOne<ProductUser>().WithOne().HasForeignKey<CustomerPreferences>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<WatchlistItem>().HasKey(x => new { x.UserId, x.Symbol });
        builder.Entity<WatchlistItem>().HasOne<ProductUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ProductSubscription>().HasKey(x => x.UserId);
        builder.Entity<ProductSubscription>().HasIndex(x => x.StripeCustomerId).IsUnique();
        builder.Entity<ProductSubscription>().HasOne<ProductUser>().WithOne().HasForeignKey<ProductSubscription>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<BillingEvent>().HasKey(x => x.Id);
        ConfigureAlertEntities(builder);
        ConfigureSupportEntities(builder);
    }

    partial void ConfigureAlertEntities(ModelBuilder builder);
    partial void ConfigureSupportEntities(ModelBuilder builder);
}

public sealed class ProductAccess(ProductDbContext db, IOptions<BillingOptions> billing, TimeProvider time)
{
    public static string? UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
    public async Task<bool> IsProAsync(string userId, CancellationToken ct = default)
    {
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        return HasPro(subscription, billing.Value.PriceId, time.GetUtcNow());
    }

    public static bool HasPro(ProductSubscription? subscription, string priceId, DateTimeOffset now) =>
        subscription is { Status: "active", LatestInvoicePaid: true, StripeSubscriptionId: not null }
        && !string.IsNullOrWhiteSpace(priceId) && subscription.PriceId == priceId
        && subscription.CurrentPeriodEnd is { } end && end > now;
}
