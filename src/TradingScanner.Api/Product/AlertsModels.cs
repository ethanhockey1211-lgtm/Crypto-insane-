using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Api.Product;

public sealed class ProductPushOptions
{
    public bool Enabled { get; set; }
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string Subject { get; set; } = "";
    public int MaximumDevices { get; set; } = 5;
    public int AlertExpirySeconds { get; set; } = 300;
    public int MaximumAttempts { get; set; } = 4;
    public int PollSeconds { get; set; } = 5;
    public int ObservationWindowMinutes { get; set; } = 120;
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(Subject);
}

public sealed class CustomerAlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string SymbolsJson { get; set; } = "[]";
    public string SetupTypesJson { get; set; } = "[]";
    public double MinimumScore { get; set; } = 60;
    public int HoldSeconds { get; set; }
    public int CooldownMinutes { get; set; } = 60;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CustomerAlertState
{
    public Guid RuleId { get; set; }
    public string Symbol { get; set; } = "";
    public DateTimeOffset? HeldSince { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool FiredWhileMatching { get; set; }
}

/// <summary>Immutable evidence captured on issue, including suppressed alerts. Delivery changes live separately.</summary>
public sealed class CustomerAlertRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string UserId { get; init; } = "";
    public Guid? RuleId { get; init; }
    public string RuleName { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string SetupType { get; init; } = "";
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string Status { get; init; } = "recorded";
    public string Message { get; init; } = "";
    public string EvidenceJson { get; init; } = "{}";
    public bool IsTest { get; init; }
}

public sealed class CustomerPushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string EndpointHash { get; set; } = "";
    // These browser-issued credentials must never be returned by list/history endpoints or logged.
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAcceptedAt { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class CustomerPushDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AlertId { get; set; }
    public Guid DeviceId { get; set; }
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Subsequent sampled quotes, never a claimed filled trade or realized performance.</summary>
public sealed class CustomerAlertOutcome
{
    public Guid AlertId { get; set; }
    public string Status { get; set; } = "monitoring";
    public string Exchange { get; set; } = "";
    public string Symbol { get; set; } = "";
    public double InitialPrice { get; set; }
    public double TargetPrice { get; set; }
    public double StopPrice { get; set; }
    public DateTimeOffset WindowEndsAt { get; set; }
    public DateTimeOffset LastObservationAt { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public double? ObservedPrice { get; set; }
    public bool DataGap { get; set; }
    public const string Interpretation = "Subsequent sampled quotes only, not an executed trade or realized return. Intrabar touches, their order, fills and slippage may be missed. Costs remain the assumptions recorded at issue.";
}

public sealed record AlertRuleRequest(string Name, bool Enabled, string[]? Symbols, string[]? SetupTypes,
    double MinimumScore = 60, int HoldSeconds = 0, int CooldownMinutes = 60);
public sealed record PushKeys(string P256dh, string Auth);
public sealed record PushSubscriptionRequest(string Endpoint, PushKeys Keys, string? DeviceName);
public sealed record PushTestRequest(Guid DeviceId);

public static class AlertJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    public static string[] Strings(string json) => JsonSerializer.Deserialize<string[]>(json, Options) ?? [];
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public partial class ProductDbContext
{
    public DbSet<CustomerAlertRule> AlertRules => Set<CustomerAlertRule>();
    public DbSet<CustomerAlertState> AlertStates => Set<CustomerAlertState>();
    public DbSet<CustomerAlertRecord> AlertRecords => Set<CustomerAlertRecord>();
    public DbSet<CustomerPushDevice> PushDevices => Set<CustomerPushDevice>();
    public DbSet<CustomerPushDelivery> PushDeliveries => Set<CustomerPushDelivery>();
    public DbSet<CustomerAlertOutcome> AlertOutcomes => Set<CustomerAlertOutcome>();

    partial void ConfigureAlertEntities(ModelBuilder builder)
    {
        builder.Entity<CustomerAlertRule>().HasKey(x => x.Id);
        builder.Entity<CustomerAlertRule>().HasIndex(x => x.UserId);
        builder.Entity<CustomerAlertRule>().HasOne<ProductUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CustomerAlertState>().HasKey(x => new { x.RuleId, x.Symbol });
        builder.Entity<CustomerAlertState>().HasOne<CustomerAlertRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CustomerAlertRecord>().HasKey(x => x.Id);
        builder.Entity<CustomerAlertRecord>().HasIndex(x => new { x.UserId, x.IssuedAt });
        builder.Entity<CustomerAlertRecord>().HasOne<ProductUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CustomerPushDevice>().HasKey(x => x.Id);
        builder.Entity<CustomerPushDevice>().HasIndex(x => x.EndpointHash).IsUnique();
        builder.Entity<CustomerPushDevice>().HasOne<ProductUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CustomerPushDelivery>().HasKey(x => x.Id);
        builder.Entity<CustomerPushDelivery>().HasIndex(x => new { x.AlertId, x.DeviceId }).IsUnique();
        builder.Entity<CustomerPushDelivery>().HasIndex(x => new { x.State, x.NextAttemptAt });
        builder.Entity<CustomerPushDelivery>().HasOne<CustomerAlertRecord>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CustomerPushDelivery>().HasOne<CustomerPushDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CustomerAlertOutcome>().HasKey(x => x.AlertId);
        builder.Entity<CustomerAlertOutcome>().HasIndex(x => x.Status);
        builder.Entity<CustomerAlertOutcome>().HasOne<CustomerAlertRecord>().WithOne().HasForeignKey<CustomerAlertOutcome>(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
        // SQLite cannot natively order DateTimeOffset. Persist UTC instants, including cooldowns, as integers.
        foreach (var type in new[] { typeof(CustomerAlertRule), typeof(CustomerAlertState), typeof(CustomerAlertRecord), typeof(CustomerPushDevice), typeof(CustomerPushDelivery), typeof(CustomerAlertOutcome) })
        foreach (var property in builder.Entity(type).Metadata.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero)));
            else if (property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(x => x.HasValue ? x.Value.UtcTicks : null, x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null));
        }
    }
}

public static class ProductAlertPolicy
{
    public static bool SetupSelected(SetupType setup, CustomerAlertRule rule, CustomerPreferences preferences)
    {
        var explicitTypes = AlertJson.Strings(rule.SetupTypesJson);
        if (explicitTypes.Length > 0) return explicitTypes.Contains(setup.ToString(), StringComparer.OrdinalIgnoreCase);
        var categories = AlertJson.Strings(preferences.ConditionsJson);
        return setup switch
        {
            SetupType.Breakout or SetupType.BreakoutRetest or SetupType.RangeBreakout => categories.Contains("breakout"),
            SetupType.TrendPullback or SetupType.VwapReclaim or SetupType.SupportBounce => categories.Contains("trend"),
            SetupType.MomentumContinuation or SetupType.VolumeExpansion or SetupType.VolatilityExpansion => categories.Contains("momentum"),
            _ => false, // Reversal can be monitored only by explicitly selecting that setup type.
        };
    }

    public static bool IsFresh(ScannerSnapshot snapshot, Opportunity opportunity, DateTimeOffset now, TimeSpan maximumAge) =>
        snapshot.At <= now.AddSeconds(2) && now - snapshot.At <= maximumAge
        && snapshot.Market.At <= now.AddSeconds(2) && now - snapshot.Market.At <= maximumAge
        && opportunity.At <= now.AddSeconds(2) && now - opportunity.At <= maximumAge
        && !opportunity.Quality.Stale && opportunity.Quality.HistoryLoaded
        && opportunity.Quality.AgeMs >= -2000
        && TimeSpan.FromMilliseconds(opportunity.Quality.AgeMs) + (now - opportunity.At) <= maximumAge;

    public static bool InQuietHours(CustomerPreferences preferences, DateTimeOffset now)
    {
        if (!preferences.QuietHoursEnabled) return false;
        if (!TimeOnly.TryParse(preferences.QuietHoursStart, out var start) || !TimeOnly.TryParse(preferences.QuietHoursEnd, out var end)) return true;
        try
        {
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(preferences.TimeZone)).DateTime);
            return start == end || (start < end ? local >= start && local < end : local >= start || local < end);
        }
        catch (TimeZoneNotFoundException) { return true; }
        catch (InvalidTimeZoneException) { return true; }
    }

    // Persist the edge and cooldown. Restarting never grants another alert for a still-matching setup.
    public static bool Advance(CustomerAlertState state, bool matches, DateTimeOffset now, int holdSeconds, int cooldownMinutes, TimeSpan maximumGap)
    {
        if (now <= state.LastSeenAt) return false;
        if (now - state.LastSeenAt > maximumGap) state.HeldSince = null;
        state.LastSeenAt = now;
        if (!matches)
        {
            state.HeldSince = null;
            state.FiredWhileMatching = false;
            return false;
        }
        if (state.FiredWhileMatching) return false;
        state.HeldSince ??= now;
        if (now - state.HeldSince.Value < TimeSpan.FromSeconds(holdSeconds)
            || (state.LastFiredAt is { } fired && now - fired < TimeSpan.FromMinutes(cooldownMinutes))) return false;
        state.FiredWhileMatching = true;
        state.LastFiredAt = now;
        state.HeldSince = null;
        return true;
    }
}
