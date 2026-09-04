using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingScanner.Core.Providers;
using TradingScanner.Infrastructure.Postgres;
using TradingScanner.Signals.Alerts;
using TradingScanner.Signals.Paper;
using TradingScanner.Signals.Performance;

namespace TradingScanner.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers PostgreSQL-backed repositories when ConnectionStrings:Postgres is set. Must be called BEFORE
    /// AddSignals so the in-memory defaults (TryAdd) are not used. Without a connection string nothing is registered
    /// and the app runs fully in memory.
    /// </summary>
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("Postgres") ?? Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(raw)) return services;
        var cs = NormalizeConnectionString(raw);
        services.AddSingleton(sp => new NpgsqlDataSourceBuilder(cs).UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>()).Build());
        services.AddSingleton<Migrator>();
        services.AddHostedService<MigrationHostedService>();
        services.AddSingleton<IAlertRepository, PostgresAlertRepository>();
        services.AddSingleton<IPaperRepository, PostgresPaperRepository>();
        services.AddSingleton<ISignalRepository, PostgresSignalRepository>();
        services.AddSingleton<CandleArchiver>();
        services.AddSingleton<IMarketEventObserver>(sp => sp.GetRequiredService<CandleArchiver>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CandleArchiver>());
        return services;
    }

    public static bool HasPostgres(this IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres") ?? Environment.GetEnvironmentVariable("DATABASE_URL"));

    /// <summary>Npgsql needs key=value form; hosted databases hand out postgres:// URLs. Convert when needed.</summary>
    public static string NormalizeConnectionString(string raw)
    {
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return raw;
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            Database = uri.AbsolutePath.TrimStart('/'),
        };
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var sslMode = query["sslmode"];
        // Hosted providers terminate TLS and expect it; keep the caller's explicit choice, default to Require off-localhost.
        b.SslMode = sslMode?.ToLowerInvariant() switch
        {
            "disable" => SslMode.Disable,
            "prefer" => SslMode.Prefer,
            "require" => SslMode.Require,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            _ => uri.Host is "localhost" or "127.0.0.1" ? SslMode.Prefer : SslMode.Require,
        };
        return b.ConnectionString;
    }

    /// <summary>Runs migrations before other hosted services start (registered first).</summary>
    private sealed class MigrationHostedService : IHostedService
    {
        private readonly Migrator _migrator;
        public MigrationHostedService(Migrator migrator) => _migrator = migrator;
        public Task StartAsync(CancellationToken cancellationToken) => _migrator.MigrateAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
