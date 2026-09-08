using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TradingScanner.Core;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Coinbase;
using TradingScanner.MarketData.Engine;
using TradingScanner.MarketData.Kraken;
using TradingScanner.MarketData.Metrics;
using TradingScanner.MarketData.Universe;
using TradingScanner.MarketData.WebSockets;

namespace TradingScanner.MarketData;

public static class ServiceCollectionExtensions
{
    /// <summary>Resolves IMarketEventObserver registrations on first enumeration so observers may depend on the engine.</summary>
    private sealed class LazyObservers(IServiceProvider provider) : IEnumerable<IMarketEventObserver>
    {
        public IEnumerator<IMarketEventObserver> GetEnumerator() => provider.GetServices<IMarketEventObserver>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static IServiceCollection AddMarketData(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MarketDataOptions>(configuration.GetSection(MarketDataOptions.SectionName));
        services.Configure<CoinbaseOptions>(configuration.GetSection(CoinbaseOptions.SectionName));
        services.Configure<KrakenOptions>(configuration.GetSection(KrakenOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<MarketDataMetrics>();
        services.AddSingleton<MarketEventChannel>();
        services.AddSingleton<UniverseState>();
        services.AddSingleton<IWebSocketClientFactory, ClientWebSocketFactory>();
        services.AddSingleton(sp => new RequestRateLimiter(sp.GetRequiredService<IOptions<MarketDataOptions>>().Value.WarmUpRequestsPerSecond, TimeSpan.FromSeconds(1), sp.GetRequiredService<TimeProvider>()));

        services.AddHttpClient<CoinbaseRestClient>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<CoinbaseOptions>>().Value;
            http.BaseAddress = new Uri(o.RestUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(o.UserAgent);
        });
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CoinbaseOptions>>().Value);
        services.AddHttpClient<KrakenRestClient>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<KrakenOptions>>().Value;
            http.BaseAddress = new Uri(o.RestUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(o.UserAgent);
        });
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<KrakenOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MarketDataOptions>>().Value);

        services.AddSingleton<IMarketDataProvider>(sp => sp.GetRequiredService<MarketDataOptions>().Provider.Trim().ToLowerInvariant() switch
        {
            KrakenExchangeProvider.ProviderName => ActivatorUtilities.CreateInstance<KrakenExchangeProvider>(sp),
            CoinbaseExchangeProvider.ProviderName => ActivatorUtilities.CreateInstance<CoinbaseExchangeProvider>(sp),
            var name => throw new InvalidOperationException($"Unsupported MarketData:Provider '{name}'. Use kraken or coinbase."),
        });

        services.AddSingleton(sp => new MarketStateEngine(
            sp.GetRequiredService<MarketEventChannel>(),
            new LazyObservers(sp),
            sp.GetRequiredService<IOptions<MarketDataOptions>>(),
            sp.GetRequiredService<MarketDataMetrics>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MarketStateEngine>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMarketStateReader>(sp => sp.GetRequiredService<MarketStateEngine>());
        services.AddSingleton<TradingScanner.Core.Market.ICandleHistoryReader>(sp => sp.GetRequiredService<MarketStateEngine>());
        services.AddSingleton<TradingScanner.Core.Market.ISymbolInfoReader>(sp => sp.GetRequiredService<MarketStateEngine>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MarketStateEngine>());
        services.AddSingleton<HistoryWarmUp>();
        services.AddHostedService<MarketDataOrchestrator>();
        return services;
    }
}
