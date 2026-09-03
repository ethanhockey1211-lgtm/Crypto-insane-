using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradingScanner.Signals;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSignals(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SignalsOptions>(configuration.GetSection(SignalsOptions.SectionName));
        services.AddSingleton<SignalsEngine>();
        services.AddSingleton<ISignalsReader>(sp => sp.GetRequiredService<SignalsEngine>());
        return services;
    }
}
