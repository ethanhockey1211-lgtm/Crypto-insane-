using System.Diagnostics.Metrics;

namespace TradingScanner.MarketData.Metrics;

/// <summary>
/// Hot-path counters (Interlocked) plus System.Diagnostics.Metrics instruments for OpenTelemetry export.
/// <see cref="Snapshot"/> is what the /api/system/metrics endpoint returns so an operator can tell whether
/// the scanner is keeping up with the market.
/// </summary>
public sealed class MarketDataMetrics
{
    public static readonly string MeterName = "TradingScanner.MarketData";
    private readonly Meter _meter = new(MeterName);

    private long _wsMessages, _wsBytes, _wsReconnects, _wsErrors, _parseErrors, _gaps, _duplicates, _unknownMessages;
    private long _trades, _tickers, _candleCloses, _lateTrades, _channelFullWaits;
    private long _latencySumMs, _latencyCount, _latencyMaxMs;
    private long _processSumUs, _processCount, _processMaxUs;
    private long _channelDepth;
    private long _restRequests, _restErrors;

    private readonly Counter<long> _wsMessagesC, _wsReconnectsC, _tradesC, _gapsC, _candleClosesC;
    private readonly Histogram<double> _latencyH, _processH;

    public MarketDataMetrics()
    {
        _wsMessagesC = _meter.CreateCounter<long>("md.ws.messages");
        _wsReconnectsC = _meter.CreateCounter<long>("md.ws.reconnects");
        _tradesC = _meter.CreateCounter<long>("md.trades");
        _gapsC = _meter.CreateCounter<long>("md.ws.gaps");
        _candleClosesC = _meter.CreateCounter<long>("engine.candle.closes");
        _latencyH = _meter.CreateHistogram<double>("md.trade.latency_ms");
        _processH = _meter.CreateHistogram<double>("engine.event.process_us");
        _meter.CreateObservableGauge("md.channel.depth", () => Interlocked.Read(ref _channelDepth));
    }

    public void WsMessage(int bytes) { Interlocked.Increment(ref _wsMessages); Interlocked.Add(ref _wsBytes, bytes); _wsMessagesC.Add(1); }
    public void WsReconnect() { Interlocked.Increment(ref _wsReconnects); _wsReconnectsC.Add(1); }
    public void WsError() => Interlocked.Increment(ref _wsErrors);
    public void ParseError() => Interlocked.Increment(ref _parseErrors);
    public void UnknownMessage() => Interlocked.Increment(ref _unknownMessages);
    public void Gap() { Interlocked.Increment(ref _gaps); _gapsC.Add(1); }
    public void Duplicate() => Interlocked.Increment(ref _duplicates);
    public void Ticker() => Interlocked.Increment(ref _tickers);
    public void ChannelFullWait() => Interlocked.Increment(ref _channelFullWaits);
    public void RestRequest() => Interlocked.Increment(ref _restRequests);
    public void RestError() => Interlocked.Increment(ref _restErrors);
    public void CandleClose() { Interlocked.Increment(ref _candleCloses); _candleClosesC.Add(1); }
    public void LateTrade() => Interlocked.Increment(ref _lateTrades);
    public void ChannelDepth(long depth) => Interlocked.Exchange(ref _channelDepth, depth);

    public void Trade(double latencyMs)
    {
        Interlocked.Increment(ref _trades);
        _tradesC.Add(1);
        _latencyH.Record(latencyMs);
        var ms = (long)Math.Max(0, latencyMs);
        Interlocked.Add(ref _latencySumMs, ms);
        Interlocked.Increment(ref _latencyCount);
        UpdateMax(ref _latencyMaxMs, ms);
    }

    public void EventProcessed(double micros)
    {
        _processH.Record(micros);
        var us = (long)micros;
        Interlocked.Add(ref _processSumUs, us);
        Interlocked.Increment(ref _processCount);
        UpdateMax(ref _processMaxUs, us);
    }

    private static void UpdateMax(ref long location, long value)
    {
        long current;
        while ((current = Interlocked.Read(ref location)) < value && Interlocked.CompareExchange(ref location, value, current) != current) { }
    }

    public MetricsSnapshot Snapshot() => new(
        WsMessages: Interlocked.Read(ref _wsMessages),
        WsBytes: Interlocked.Read(ref _wsBytes),
        WsReconnects: Interlocked.Read(ref _wsReconnects),
        WsErrors: Interlocked.Read(ref _wsErrors),
        ParseErrors: Interlocked.Read(ref _parseErrors),
        UnknownMessages: Interlocked.Read(ref _unknownMessages),
        Gaps: Interlocked.Read(ref _gaps),
        Duplicates: Interlocked.Read(ref _duplicates),
        Trades: Interlocked.Read(ref _trades),
        Tickers: Interlocked.Read(ref _tickers),
        CandleCloses: Interlocked.Read(ref _candleCloses),
        LateTrades: Interlocked.Read(ref _lateTrades),
        ChannelDepth: Interlocked.Read(ref _channelDepth),
        ChannelFullWaits: Interlocked.Read(ref _channelFullWaits),
        RestRequests: Interlocked.Read(ref _restRequests),
        RestErrors: Interlocked.Read(ref _restErrors),
        AvgTradeLatencyMs: Interlocked.Read(ref _latencyCount) == 0 ? 0 : (double)Interlocked.Read(ref _latencySumMs) / Interlocked.Read(ref _latencyCount),
        MaxTradeLatencyMs: Interlocked.Read(ref _latencyMaxMs),
        AvgEventProcessUs: Interlocked.Read(ref _processCount) == 0 ? 0 : (double)Interlocked.Read(ref _processSumUs) / Interlocked.Read(ref _processCount),
        MaxEventProcessUs: Interlocked.Read(ref _processMaxUs));
}

public sealed record MetricsSnapshot(
    long WsMessages, long WsBytes, long WsReconnects, long WsErrors, long ParseErrors, long UnknownMessages,
    long Gaps, long Duplicates, long Trades, long Tickers, long CandleCloses, long LateTrades,
    long ChannelDepth, long ChannelFullWaits, long RestRequests, long RestErrors,
    double AvgTradeLatencyMs, long MaxTradeLatencyMs, double AvgEventProcessUs, long MaxEventProcessUs);
