using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TradingScanner.Api.Contracts;
using TradingScanner.Api.Hubs;
using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;
using TradingScanner.MarketData.Engine;

namespace TradingScanner.Api.Services;

/// <summary>
/// Bridges the engine (which calls the observer methods inline on its thread) to SignalR.
/// Quotes are coalesced per symbol and flushed on a timer so thousands of ticks/second become a few
/// small batches; candle closes and status changes are forwarded through a queue so the engine never awaits I/O.
/// </summary>
public sealed class MarketBroadcaster : BackgroundService, IMarketEventObserver
{
    private readonly IHubContext<MarketHub> _hub;
    private readonly IMarketStateReader _reader;
    private readonly UniverseState _universe;
    private readonly IMarketDataProvider _provider;
    private readonly MarketDataOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MarketBroadcaster> _logger;

    private readonly ConcurrentDictionary<Symbol, PriceQuote> _pendingQuotes = new();
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });

    public TimeSpan QuoteFlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public MarketBroadcaster(
        IHubContext<MarketHub> hub,
        IMarketStateReader reader,
        UniverseState universe,
        IMarketDataProvider provider,
        IOptions<MarketDataOptions> options,
        ILogger<MarketBroadcaster> logger,
        TimeProvider? time = null)
    {
        _hub = hub;
        _reader = reader;
        _universe = universe;
        _provider = provider;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public void OnQuote(PriceQuote quote) => _pendingQuotes[quote.Symbol] = quote;
    public void OnCandleClosed(in Candle candle) => _events.Writer.TryWrite(candle);
    public void OnFeedStatus(FeedStatusChange change) => _events.Writer.TryWrite(change);
    public void OnGap(DataGap gap) => _events.Writer.TryWrite(gap);
    public void OnHistoryApplied(Symbol symbol) { }

    public FeedStatusDto BuildFeedStatus()
    {
        var now = _time.GetUtcNow();
        var status = _reader.FeedStatus;
        return new FeedStatusDto(
            _provider.Name,
            _provider.Exchange,
            status.ToString(),
            status is FeedStatus.Connected or FeedStatus.Degraded,
            _reader.ConnectionStatus.ToDictionary(kv => kv.Key, kv => kv.Value.Status.ToString()),
            _reader.LastEventAt is { } le ? (long)(now - le).TotalMilliseconds : null,
            _reader.LastTradeAt is { } lt ? (long)(now - lt).TotalMilliseconds : null,
            _universe.Products.Count,
            _universe.SelectedAt,
            _reader.EngineErrors);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var quotes = FlushQuotesLoopAsync(stoppingToken);
        var events = ForwardEventsLoopAsync(stoppingToken);
        await Task.WhenAll(quotes, events);
    }

    private async Task FlushQuotesLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(QuoteFlushInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_pendingQuotes.IsEmpty) continue;
                var now = _time.GetUtcNow();
                var batch = new List<QuoteDto>(_pendingQuotes.Count);
                foreach (var key in _pendingQuotes.Keys)
                {
                    if (_pendingQuotes.TryRemove(key, out var q)) batch.Add(QuoteDto.From(q, now, _options.StaleQuoteThreshold));
                }
                if (batch.Count == 0) continue;
                try
                {
                    await _hub.Clients.All.SendAsync("quotes", batch, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Quote broadcast failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ForwardEventsLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _events.Reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (evt)
                    {
                        case Candle c:
                            await _hub.Clients.Group(MarketHub.CandleGroup(c.Symbol, c.Timeframe))
                                .SendAsync("candle", new CandleClosedDto(c.Symbol.Value, c.Timeframe.Label(), CandleDto.From(c)), ct);
                            break;
                        case FeedStatusChange:
                            await _hub.Clients.All.SendAsync("feed", BuildFeedStatus(), ct);
                            break;
                        case DataGap g:
                            await _hub.Clients.All.SendAsync("gap", new GapDto(g.Symbol.Value, g.ExpectedTradeId, g.ReceivedTradeId, g.MissedTrades, g.At), ct);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Event broadcast failed for {Type}", evt.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
