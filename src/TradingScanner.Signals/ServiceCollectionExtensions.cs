using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TradingScanner.Signals.Alerts;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Signals;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSignals(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SignalsOptions>(configuration.GetSection(SignalsOptions.SectionName));
        services.AddSingleton<SignalsEngine>();
        services.AddSingleton<ISignalsReader>(sp => sp.GetRequiredService<SignalsEngine>());
        services.AddSingleton<IOptions<ScannerOptions>>(sp => Options.Create(sp.GetRequiredService<IOptions<SignalsOptions>>().Value.Scanner));
        services.AddSingleton<ScannerService>();
        services.AddSingleton<IScannerReader>(sp => sp.GetRequiredService<ScannerService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ScannerService>());
        services.TryAddSingleton<IAlertRepository, InMemoryAlertRepository>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AlertService>());
        return services;
    }
}
