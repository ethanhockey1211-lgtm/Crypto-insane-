using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingScanner.Core.Providers;

namespace TradingScanner.Analytics;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAnalytics(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AnalyticsOptions>(configuration.GetSection(AnalyticsOptions.SectionName));
        services.AddSingleton<AnalyticsEngine>();
        services.AddSingleton<IAnalyticsReader>(sp => sp.GetRequiredService<AnalyticsEngine>());
        services.AddSingleton<IMarketEventObserver>(sp => sp.GetRequiredService<AnalyticsEngine>());
        return services;
    }
}
