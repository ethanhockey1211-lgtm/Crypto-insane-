using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Core;
using TradingScanner.Core.Providers;
using TradingScanner.Core.Time;
using TradingScanner.MarketData.Universe;

namespace TradingScanner.MarketData.Engine;

/// <summary>
/// Startup sequence: select universe → register symbols → start streaming → warm history concurrently.
/// Universe refresh (adding/removing symbols at runtime) is deliberately not in Phase 1; it requires resubscribe support.
/// </summary>
public sealed class MarketDataOrchestrator : BackgroundService
{
    private readonly IMarketDataProvider _provider;
    private readonly MarketStateEngine _engine;
    private readonly MarketEventChannel _channel;
    private readonly UniverseState _universe;
    private readonly HistoryWarmUp _warmUp;
    private readonly MarketDataOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MarketDataOrchestrator> _logger;

    public MarketDataOrchestrator(
        IMarketDataProvider provider,
        MarketStateEngine engine,
        MarketEventChannel channel,
        UniverseState universe,
        HistoryWarmUp warmUp,
        IOptions<MarketDataOptions> options,
        ILogger<MarketDataOrchestrator> logger,
        TimeProvider? time = null)
    {
        _provider = provider;
        _engine = engine;
        _channel = channel;
        _universe = universe;
        _warmUp = warmUp;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));
        IReadOnlyList<ProductInfo>? products = null;
        while (products is null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                products = await _provider.GetProductsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                var delay = backoff.Next();
                _logger.LogError(ex, "Product list unavailable from {Provider}; retrying in {Delay}s", _provider.Name, delay.TotalSeconds);
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        if (products is null) return;

        var universe = UniverseSelector.Select(products, _options);
        _universe.Set(universe, _time.GetUtcNow());
        var symbols = universe.Select(p => p.Symbol).ToArray();
        _engine.RegisterSymbols(symbols);
        _logger.LogInformation("Universe: {Count} symbols on {Exchange} (top by 24h {Quote} volume; min {Min:N0}). Top 10: {Top}",
            symbols.Length, _provider.Exchange, _options.QuoteCurrency, _options.MinVolume24hQuote,
            string.Join(", ", symbols.Take(10).Select(s => s.Value)));

        if (symbols.Length == 0)
        {
            _logger.LogError("Empty universe; nothing to stream. Check MarketData:MinVolume24hQuote and QuoteCurrency.");
            return;
        }

        var streaming = _provider.RunAsync(symbols, _channel.Writer, stoppingToken);
        var warm = _options.WarmUpHistory ? _warmUp.WarmUpAsync(symbols, stoppingToken) : Task.CompletedTask;

        try
        {
            await Task.WhenAll(streaming, warm).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
