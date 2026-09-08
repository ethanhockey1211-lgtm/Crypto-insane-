using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.Core.Time;
using TradingScanner.MarketData.Metrics;

namespace TradingScanner.MarketData.Kraken;

public sealed class KrakenExchangeProvider : IMarketDataProvider
{
    public const string ProviderName = "kraken";
    public const string ExchangeName = "Kraken";
    private static readonly HashSet<Timeframe> Historical = [Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4];
    private readonly KrakenOptions _options;
    private readonly MarketDataOptions _marketOptions;
    private readonly KrakenRestClient _rest;
    private readonly IWebSocketClientFactory _sockets;
    private readonly MarketDataMetrics _metrics;
    private readonly ILogger<KrakenExchangeProvider> _logger;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private FeedStatus[] _connectionStatus = [];
    private readonly Dictionary<Symbol, long> _lastTradeId = new();
    private readonly object _tradeGate = new();

    public KrakenExchangeProvider(KrakenOptions options, MarketDataOptions marketOptions, KrakenRestClient rest,
        IWebSocketClientFactory sockets, MarketDataMetrics metrics, ILogger<KrakenExchangeProvider> logger,
        TimeProvider? time = null, Random? random = null)
    {
        _options = options; _marketOptions = marketOptions; _rest = rest; _sockets = sockets;
        _metrics = metrics; _logger = logger; _time = time ?? TimeProvider.System; _random = random ?? Random.Shared;
    }

    public string Name => ProviderName;
    public string Exchange => ExchangeName;
    public IReadOnlySet<Timeframe> HistoricalTimeframes => Historical;
    public FeedStatus Status
    {
        get
        {
            var states = Volatile.Read(ref _connectionStatus);
            if (states.Length == 0) return FeedStatus.Disconnected;
            if (states.All(s => s == FeedStatus.Connected)) return FeedStatus.Connected;
            if (states.Any(s => s is FeedStatus.Connected or FeedStatus.Degraded)) return FeedStatus.Degraded;
            if (states.Contains(FeedStatus.Reconnecting)) return FeedStatus.Reconnecting;
            return states.Contains(FeedStatus.Connecting) ? FeedStatus.Connecting : FeedStatus.Disconnected;
        }
    }

    public Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct) => _rest.GetProductsAsync(ct);
    public Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(Symbol symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        _rest.GetCandlesAsync(symbol, timeframe, from, to, ct);

    public async Task RunAsync(IReadOnlyCollection<Symbol> symbols, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        if (symbols.Count == 0) throw new ArgumentException("No Kraken symbols to subscribe.", nameof(symbols));
        var shards = symbols.Distinct().Chunk(Math.Max(1, _marketOptions.SymbolsPerConnection)).ToArray();
        _connectionStatus = new FeedStatus[shards.Length];
        try { await Task.WhenAll(shards.Select((shard, index) => ConnectionLoopAsync(index, shard, output, ct))).ConfigureAwait(false); }
        finally { Array.Fill(_connectionStatus, FeedStatus.Disconnected); }
    }

    private async Task ConnectionLoopAsync(int index, Symbol[] symbols, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        var backoff = new Backoff(_marketOptions.ReconnectInitialDelay, _marketOptions.ReconnectMaxDelay, _random);
        var firstAttempt = true;
        while (!ct.IsCancellationRequested)
        {
            if (!firstAttempt)
            {
                _metrics.WsReconnect();
                var delay = backoff.Next();
                await SetStatusAsync(index, FeedStatus.Reconnecting, $"retry in {delay.TotalSeconds:F1}s", output, ct).ConfigureAwait(false);
                try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
            firstAttempt = false;
            using var socket = _sockets.Create();
            var connectedAt = _time.GetTimestamp();
            try
            {
                await SetStatusAsync(index, FeedStatus.Connecting, null, output, ct).ConfigureAwait(false);
                await socket.ConnectAsync(new Uri(_options.WebSocketUrl), ct).ConfigureAwait(false);
                await socket.SendTextAsync(KrakenMessageParser.BuildSubscribe(symbols, "trade"), ct).ConfigureAwait(false);
                await socket.SendTextAsync(KrakenMessageParser.BuildSubscribe(symbols, "ticker"), ct).ConfigureAwait(false);
                connectedAt = _time.GetTimestamp();
                await ReadLoopAsync(index, symbols, socket, output, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _metrics.WsError();
                _logger.LogWarning(ex, "Kraken connection {Index} failed: {Message}", index, ex.Message);
            }
            finally
            {
                // Abort directly so shutdown cannot block on an unresponsive server or a full output channel.
                socket.Abort();
            }
            if (_time.GetElapsedTime(connectedAt) > _marketOptions.ReconnectMaxDelay) backoff.Reset();
        }
        _connectionStatus[index] = FeedStatus.Disconnected;
        output.TryWrite(MarketEvent.FromStatus(new FeedStatusChange(ProviderName, index, FeedStatus.Disconnected, "stopped", _time.GetUtcNow())));
    }

    private async Task ReadLoopAsync(int index, Symbol[] symbols, IWebSocketClient socket, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var subscribed = symbols.ToHashSet();
        var pending = symbols.SelectMany(s => new[] { "trade:" + KrakenSymbols.ToWebSocket(s), "ticker:" + KrakenSymbols.ToWebSocket(s) }).ToHashSet(StringComparer.Ordinal);
        var started = _time.GetTimestamp();
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (pending.Count > 0 && _time.GetElapsedTime(started) > _marketOptions.ReceiveTimeout)
                    throw new TimeoutException($"Kraken did not confirm {pending.Count} subscriptions.");
                receiveCts.CancelAfter(_marketOptions.ReceiveTimeout);
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        if (length >= 4 * 1024 * 1024) throw new FormatException("Kraken message exceeds 4 MiB.");
                        var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, length).CopyTo(bigger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }
                    try { result = await socket.ReceiveAsync(buffer.AsMemory(length), receiveCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { throw new TimeoutException($"No Kraken message within {_marketOptions.ReceiveTimeout.TotalSeconds:F1}s."); }
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    length += result.Count;
                } while (!result.EndOfMessage);
                receiveCts.CancelAfter(Timeout.InfiniteTimeSpan);
                _metrics.WsMessage(length);
                var message = KrakenMessageParser.Parse(buffer.AsMemory(0, length), _time.GetUtcNow());
                if (message.Error is { } error) throw new InvalidOperationException("Kraken feed: " + error);
                if (message.Subscription)
                {
                    if (pending.Remove(message.Channel + ":" + message.AcknowledgedSymbol) && pending.Count == 0)
                        await SetStatusAsync(index, FeedStatus.Connected, null, output, ct).ConfigureAwait(false);
                    continue;
                }
                foreach (var ticker in message.Tickers)
                {
                    if (!subscribed.Contains(ticker.Symbol)) continue;
                    _metrics.Ticker();
                    await output.WriteAsync(MarketEvent.FromTicker(ticker), ct).ConfigureAwait(false);
                }
                foreach (var trade in message.Trades.OrderBy(t => t.TradeId))
                {
                    if (!subscribed.Contains(trade.Symbol)) continue;
                    if (message.Type == "snapshot")
                    {
                        // A historical snapshot must never count toward live candle volume. Preserve the reconnect watermark.
                        lock (_tradeGate) _lastTradeId.TryAdd(trade.Symbol, message.Trades.Where(t => t.Symbol == trade.Symbol).Max(t => t.TradeId));
                        await output.WriteAsync(MarketEvent.FromQuoteOnly(trade), ct).ConfigureAwait(false);
                        continue;
                    }
                    DataGap? gap = null;
                    lock (_tradeGate)
                    {
                        if (_lastTradeId.TryGetValue(trade.Symbol, out var last))
                        {
                            if (trade.TradeId <= last) { _metrics.Duplicate(); continue; }
                            if (trade.TradeId != last + 1) gap = new DataGap(ProviderName, trade.Symbol, last + 1, trade.TradeId, _time.GetUtcNow());
                        }
                        _lastTradeId[trade.Symbol] = trade.TradeId;
                    }
                    if (gap is not null)
                    {
                        _metrics.Gap();
                        await SetStatusAsync(index, FeedStatus.Degraded, "missing trades", output, ct).ConfigureAwait(false);
                        await output.WriteAsync(MarketEvent.FromGap(gap), ct).ConfigureAwait(false);
                    }
                    _metrics.Trade(trade.Latency.TotalMilliseconds);
                    await output.WriteAsync(MarketEvent.FromTrade(trade), ct).ConfigureAwait(false);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async ValueTask SetStatusAsync(int index, FeedStatus status, string? reason, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        _connectionStatus[index] = status;
        try { await output.WriteAsync(MarketEvent.FromStatus(new FeedStatusChange(ProviderName, index, status, reason, _time.GetUtcNow())), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
