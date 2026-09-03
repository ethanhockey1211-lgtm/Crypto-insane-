using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.Core.Time;
using TradingScanner.MarketData.Metrics;

namespace TradingScanner.MarketData.Coinbase;

/// <summary>
/// Coinbase Exchange market-data adapter. Streams the public "matches", "ticker" and "heartbeat" channels,
/// shards symbols across several sockets, detects missed trades via per-product trade_id continuity,
/// reconnects with jittered exponential backoff, and resubscribes.
/// </summary>
public sealed class CoinbaseExchangeProvider : IMarketDataProvider
{
    public const string ProviderName = "coinbase";
    public const string ExchangeName = "Coinbase Exchange";
    private static readonly string[] Channels = ["matches", "ticker", "heartbeat"];
    private static readonly HashSet<Timeframe> Historical = [Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.H1];

    private readonly CoinbaseOptions _options;
    private readonly MarketDataOptions _mdOptions;
    private readonly CoinbaseRestClient _rest;
    private readonly IWebSocketClientFactory _sockets;
    private readonly MarketDataMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<CoinbaseExchangeProvider> _logger;
    private readonly Random _random;

    private FeedStatus[] _connectionStatus = [];
    private readonly Dictionary<string, long> _lastTradeId = new(StringComparer.Ordinal);
    private readonly object _tradeIdGate = new();

    public CoinbaseExchangeProvider(
        CoinbaseOptions options,
        MarketDataOptions mdOptions,
        CoinbaseRestClient rest,
        IWebSocketClientFactory sockets,
        MarketDataMetrics metrics,
        ILogger<CoinbaseExchangeProvider> logger,
        TimeProvider? time = null,
        Random? random = null)
    {
        _options = options;
        _mdOptions = mdOptions;
        _rest = rest;
        _sockets = sockets;
        _metrics = metrics;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared;
    }

    public string Name => ProviderName;
    public string Exchange => ExchangeName;
    public IReadOnlySet<Timeframe> HistoricalTimeframes => Historical;

    public FeedStatus Status
    {
        get
        {
            var s = Volatile.Read(ref _connectionStatus);
            if (s.Length == 0) return FeedStatus.Disconnected;
            if (s.All(x => x == FeedStatus.Connected)) return FeedStatus.Connected;
            if (s.Any(x => x is FeedStatus.Connected or FeedStatus.Degraded)) return FeedStatus.Degraded;
            if (s.Any(x => x == FeedStatus.Reconnecting)) return FeedStatus.Reconnecting;
            if (s.Any(x => x == FeedStatus.Connecting)) return FeedStatus.Connecting;
            return FeedStatus.Disconnected;
        }
    }

    public async Task<IReadOnlyList<ProductInfo>> GetProductsAsync(CancellationToken ct)
    {
        var products = await _rest.GetProductsAsync(ct).ConfigureAwait(false);
        var candidates = products
            .Where(p => p.IsOnline && string.Equals(p.QuoteCurrency, _mdOptions.QuoteCurrency, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _logger.LogInformation("Coinbase lists {Total} products, {Candidates} online {Quote} pairs; fetching 24h stats", products.Count, candidates.Count, _mdOptions.QuoteCurrency);
        var enriched = await _rest.EnrichWithStatsAsync(candidates, _mdOptions.WarmUpRequestsPerSecond, ct).ConfigureAwait(false);
        var others = products.Except(candidates);
        return enriched.Concat(others).ToList();
    }

    public Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(Symbol symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        _rest.GetCandlesAsync(symbol, timeframe, from, to, ct).ContinueWith(t => (IReadOnlyList<Candle>)t.Result, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    public async Task RunAsync(IReadOnlyCollection<Symbol> symbols, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        if (symbols.Count == 0) throw new ArgumentException("No symbols to subscribe.", nameof(symbols));
        var shards = symbols.Select(s => s.Value).Chunk(Math.Max(1, _mdOptions.SymbolsPerConnection)).ToArray();
        _connectionStatus = new FeedStatus[shards.Length];
        _logger.LogInformation("Coinbase provider starting {Connections} connection(s) for {Symbols} symbols", shards.Length, symbols.Count);

        var tasks = shards.Select((shard, i) => ConnectionLoopAsync(i, shard, output, ct)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            for (var i = 0; i < _connectionStatus.Length; i++) _connectionStatus[i] = FeedStatus.Disconnected;
        }
    }

    private async Task ConnectionLoopAsync(int index, string[] products, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        var backoff = new Backoff(_mdOptions.ReconnectInitialDelay, _mdOptions.ReconnectMaxDelay, _random);
        var uri = new Uri(_options.WebSocketUrl);
        var subscribe = CoinbaseMessageParser.BuildSubscribe(products, Channels);
        var firstAttempt = true;

        while (!ct.IsCancellationRequested)
        {
            if (!firstAttempt)
            {
                _metrics.WsReconnect();
                var delay = backoff.Next();
                await SetStatusAsync(index, FeedStatus.Reconnecting, $"retry #{backoff.Attempt} in {delay.TotalSeconds:F1}s", output, ct).ConfigureAwait(false);
                try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            firstAttempt = false;

            using var ws = _sockets.Create();
            var connectedAt = _time.GetTimestamp();
            try
            {
                await SetStatusAsync(index, FeedStatus.Connecting, null, output, ct).ConfigureAwait(false);
                await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
                await ws.SendTextAsync(subscribe, ct).ConfigureAwait(false);
                connectedAt = _time.GetTimestamp();
                await ReadLoopAsync(index, ws, output, ct).ConfigureAwait(false);
                // ReadLoop returned => remote closed cleanly.
                _logger.LogWarning("Coinbase connection {Index} closed by remote", index);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.WsError();
                _logger.LogWarning(ex, "Coinbase connection {Index} failed: {Message}", index, ex.Message);
            }
            finally
            {
                try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnecting", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* best-effort close; Abort follows */ }
                ws.Abort();
            }

            // A connection that stayed up longer than the max backoff is considered healthy: restart the backoff ladder.
            if (_time.GetElapsedTime(connectedAt) > _mdOptions.ReconnectMaxDelay) backoff.Reset();
        }

        await SetStatusAsync(index, FeedStatus.Disconnected, "stopped", output, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(int index, IWebSocketClient ws, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (true)
            {
                receiveCts.CancelAfter(_mdOptions.ReceiveTimeout);
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan().CopyTo(bigger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }
                    try
                    {
                        result = await ws.ReceiveAsync(buffer.AsMemory(length), receiveCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No message from Coinbase in {_mdOptions.ReceiveTimeout.TotalSeconds:F0}s (heartbeat expected every second).");
                    }
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    length += result.Count;
                } while (!result.EndOfMessage);

                _metrics.WsMessage(length);
                await HandleMessageAsync(index, buffer.AsMemory(0, length), output, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            receiveCts.CancelAfter(Timeout.InfiniteTimeSpan);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask HandleMessageAsync(int index, ReadOnlyMemory<byte> utf8, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        var receivedAt = _time.GetUtcNow();
        if (!CoinbaseMessageParser.TryParse(utf8.Span, out var msg, out var error))
        {
            _metrics.ParseError();
            _logger.LogWarning("Coinbase parse error on connection {Index}: {Error}. Payload: {Payload}", index, error, CoinbaseMessageParser.DebugString(utf8.Span));
            return;
        }

        switch (msg.Type)
        {
            case CoinbaseMessageType.Match:
            {
                var symbol = new Symbol(msg.ProductId!);
                var gap = CheckContinuity(msg.ProductId!, msg.TradeId, out var duplicate);
                if (duplicate) { _metrics.Duplicate(); return; }
                if (gap is not null)
                {
                    _metrics.Gap();
                    _logger.LogWarning("Coinbase gap on {Symbol}: expected trade {Expected}, got {Got} ({Missed} missed)", symbol, gap.ExpectedTradeId, gap.ReceivedTradeId, gap.MissedTrades);
                    await WriteAsync(output, MarketEvent.FromGap(gap with { Symbol = symbol }), ct).ConfigureAwait(false);
                    SetStatusLocal(index, FeedStatus.Degraded);
                }
                var trade = new Trade(symbol, ProviderName, ExchangeName, msg.TradeId, msg.Price, msg.Size, msg.TakerSide, msg.Time, receivedAt);
                _metrics.Trade(trade.Latency.TotalMilliseconds);
                await WriteAsync(output, MarketEvent.FromTrade(trade), ct).ConfigureAwait(false);
                break;
            }
            case CoinbaseMessageType.LastMatch:
            {
                // Sent once per product on subscribe: the most recent trade BEFORE our subscription. Price only; never volume.
                var symbol = new Symbol(msg.ProductId!);
                lock (_tradeIdGate) _lastTradeId.TryAdd(msg.ProductId!, msg.TradeId);
                var trade = new Trade(symbol, ProviderName, ExchangeName, msg.TradeId, msg.Price, msg.Size, msg.TakerSide, msg.Time, receivedAt);
                await WriteAsync(output, MarketEvent.FromQuoteOnly(trade), ct).ConfigureAwait(false);
                break;
            }
            case CoinbaseMessageType.Ticker:
            {
                _metrics.Ticker();
                var symbol = new Symbol(msg.ProductId!);
                var ticker = new TickerUpdate(symbol, ProviderName, ExchangeName, msg.Price, msg.BestBid, msg.BestAsk, msg.Open24h, msg.High24h, msg.Low24h, msg.Volume24h, msg.Time, receivedAt);
                await WriteAsync(output, MarketEvent.FromTicker(ticker), ct).ConfigureAwait(false);
                break;
            }
            case CoinbaseMessageType.Heartbeat:
                // Liveness only. Heartbeat.last_trade_id is NOT used for gap detection because a match with that id
                // may still be in flight; trade_id continuity on the matches themselves is authoritative.
                break;
            case CoinbaseMessageType.Subscriptions:
                _logger.LogInformation("Coinbase connection {Index} subscribed ({Products} products)", index, msg.SubscribedProducts);
                await SetStatusAsync(index, FeedStatus.Connected, null, output, ct).ConfigureAwait(false);
                break;
            case CoinbaseMessageType.Error:
                throw new InvalidOperationException($"Coinbase feed error: {msg.ErrorMessage} ({msg.ErrorReason})");
            default:
                _metrics.UnknownMessage();
                break;
        }
    }

    private DataGap? CheckContinuity(string productId, long tradeId, out bool duplicate)
    {
        duplicate = false;
        lock (_tradeIdGate)
        {
            if (_lastTradeId.TryGetValue(productId, out var last))
            {
                if (tradeId <= last) { duplicate = true; return null; }
                _lastTradeId[productId] = tradeId;
                if (tradeId != last + 1)
                    return new DataGap(ProviderName, new Symbol(productId), last + 1, tradeId, _time.GetUtcNow());
                return null;
            }
            _lastTradeId[productId] = tradeId;
            return null;
        }
    }

    private static async ValueTask WriteAsync(ChannelWriter<MarketEvent> output, MarketEvent evt, CancellationToken ct)
    {
        if (!output.TryWrite(evt)) await output.WriteAsync(evt, ct).ConfigureAwait(false);
    }

    private void SetStatusLocal(int index, FeedStatus status)
    {
        var arr = Volatile.Read(ref _connectionStatus);
        if (index < arr.Length) arr[index] = status;
    }

    private async ValueTask SetStatusAsync(int index, FeedStatus status, string? reason, ChannelWriter<MarketEvent> output, CancellationToken ct)
    {
        SetStatusLocal(index, status);
        var change = new FeedStatusChange(ProviderName, index, status, reason, _time.GetUtcNow());
        if (status is FeedStatus.Connected) _logger.LogInformation("Coinbase connection {Index}: {Status}", index, status);
        else _logger.LogWarning("Coinbase connection {Index}: {Status} {Reason}", index, status, reason);
        try { await WriteAsync(output, MarketEvent.FromStatus(change), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
