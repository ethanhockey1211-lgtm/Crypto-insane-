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
        var cs = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(cs)) return services;
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

    public static bool HasPostgres(this IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres"));

    /// <summary>Runs migrations before other hosted services start (registered first).</summary>
    private sealed class MigrationHostedService : IHostedService
    {
        private readonly Migrator _migrator;
        public MigrationHostedService(Migrator migrator) => _migrator = migrator;
        public Task StartAsync(CancellationToken cancellationToken) => _migrator.MigrateAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
