using TradingScanner.Core.Market;

namespace TradingScanner.Core;

public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>Provider name to use as the primary feed.</summary>
    public string Provider { get; set; } = "coinbase";

    /// <summary>Quote currency that defines the tradable universe.</summary>
    public string QuoteCurrency { get; set; } = "USD";

    /// <summary>List every online pair in the quote currency, including stablecoins and pairs without volume statistics.
    /// Bypasses UniverseSize, MinVolume24hQuote and ExcludedBases; execution checks still apply separately.</summary>
    public bool IncludeAllPairs { get; set; }

    /// <summary>Universe size after ranking by 24h quote volume.</summary>
    public int UniverseSize { get; set; } = 200;

    /// <summary>Minimum 24h quote volume for inclusion (in quote currency).</summary>
    public decimal MinVolume24hQuote { get; set; } = 1_000_000m;

    /// <summary>Base currencies never included (stablecoins, wrapped fiat).</summary>
    public string[] ExcludedBases { get; set; } = ["USDT", "USDC", "DAI", "PYUSD", "EURC", "GUSD", "PAX", "USDP", "TUSD", "BUSD", "WBTC", "CBETH", "WETH"];

    /// <summary>Symbols always included regardless of volume ranking (BTC and ETH drive the regime model).</summary>
    public string[] AlwaysInclude { get; set; } = ["BTC-USD", "ETH-USD"];

    /// <summary>Symbols per WebSocket connection. Sharding limits blast radius of a single dropped socket.</summary>
    public int SymbolsPerConnection { get; set; } = 60;

    /// <summary>How long after a 1m boundary to wait for late trades before the bar is closed by the clock.</summary>
    public TimeSpan CandleCloseGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Quotes older than this are flagged stale in the API.</summary>
    public TimeSpan StaleQuoteThreshold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>No message (including heartbeat) for this long forces a reconnect.</summary>
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bounded channel capacity between providers and the engine. Growth = the engine is behind.</summary>
    public int IngestChannelCapacity { get; set; } = 100_000;

    /// <summary>Whether to warm candle series from REST history at startup.</summary>
    public bool WarmUpHistory { get; set; } = true;

    /// <summary>Concurrent REST requests during warm-up (Coinbase public limit is ~10 req/s).</summary>
    public int WarmUpRequestsPerSecond { get; set; } = 8;

    public Dictionary<Timeframe, int> SeriesCapacity { get; set; } = new()
    {
        [Timeframe.M1] = 1440,
        [Timeframe.M3] = 480,
        [Timeframe.M5] = 576,
        [Timeframe.M15] = 384,
        [Timeframe.M30] = 336,
        [Timeframe.H1] = 336,
        [Timeframe.H4] = 180,
    };

    public int CapacityFor(Timeframe tf) => SeriesCapacity.TryGetValue(tf, out var c) ? c : 300;
}
