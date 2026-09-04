using TradingScanner.Core.Market;

namespace TradingScanner.Api.Contracts;

/// <summary>Compact quote for the batched real-time stream and REST. Every price carries provenance and age.</summary>
public sealed record QuoteDto(
    string Symbol,
    decimal Price,
    decimal Bid,
    decimal Ask,
    long ExchangeTimeMs,
    long ReceivedAtMs,
    long AgeMs,
    bool Stale,
    string Provider,
    string Exchange)
{
    public static QuoteDto From(PriceQuote q, DateTimeOffset now, TimeSpan staleThreshold) => new(
        q.Symbol.Value, q.Price, q.Bid, q.Ask,
        q.ExchangeTime.ToUnixTimeMilliseconds(), q.ReceivedAt.ToUnixTimeMilliseconds(),
        (long)q.Age(now).TotalMilliseconds, q.IsStale(now, staleThreshold), q.Provider, q.Exchange);
}

public sealed record SymbolSummaryDto(
    string Symbol,
    QuoteDto? Quote,
    decimal? Open24h,
    decimal? High24h,
    decimal? Low24h,
    decimal? Volume24hBase,
    decimal? Change24hPct,
    long TradesSeen,
    bool HistoryLoaded);

public sealed record CandleDto(long T, decimal O, decimal H, decimal L, decimal C, decimal V, decimal QV, decimal BV, decimal SV, int N, string Src)
{
    public static CandleDto From(in Candle c) => new(
        c.OpenTime.ToUnixTimeSeconds(), c.Open, c.High, c.Low, c.Close, c.Volume, c.QuoteVolume, c.BuyVolume, c.SellVolume, c.TradeCount,
        c.Source switch { CandleSource.Live => "live", CandleSource.Historical => "hist", _ => "synthetic" });
}

public sealed record CandlesResponse(string Symbol, string Timeframe, CandleDto[] Candles, CandleDto? Forming);

public sealed record CandleClosedDto(string Symbol, string Timeframe, CandleDto Candle);

public sealed record FeedStatusDto(
    string Provider,
    string Exchange,
    string Status,
    bool Live,
    Dictionary<int, string> Connections,
    long? LastEventAgeMs,
    long? LastTradeAgeMs,
    int UniverseSize,
    DateTimeOffset? UniverseSelectedAt,
    long EngineErrors,
    string StartupPhase,
    string? StartupError,
    int StartupAttempts);

public sealed record GapDto(string Symbol, long ExpectedTradeId, long ReceivedTradeId, long MissedTrades, DateTimeOffset At);
