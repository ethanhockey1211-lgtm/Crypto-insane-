using TradingScanner.Core;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Candles;

namespace TradingScanner.MarketData.Engine;

/// <summary>Latest 24h statistics from the ticker stream. Immutable so readers can grab it without locks.</summary>
public sealed record MarketStats(decimal Open24h, decimal High24h, decimal Low24h, decimal Volume24hBase, DateTimeOffset At);

/// <summary>
/// All in-memory state for one symbol: quote, 24h stats, candle series for every timeframe, and the builders
/// that produce them. Mutated only by the engine thread; readers use <see cref="Quote"/>, <see cref="Stats"/>
/// and <see cref="CandleSeries.Snapshot"/>.
/// </summary>
public sealed class SymbolState
{
    private static readonly Timeframe[] Timeframes = TimeframeExtensions.All;
    private static readonly Timeframe[] Higher = Timeframes.Where(t => t != Timeframe.M1).ToArray();

    private readonly CandleSeries[] _series;
    private readonly CandleBuilder _builder;
    private readonly TimeframeAggregator[] _aggregators;
    private readonly List<Candle> _scratch = new(8);
    private PriceQuote? _quote;
    private MarketStats? _stats;

    public Symbol Symbol { get; }
    public PriceQuote? Quote => Volatile.Read(ref _quote);
    public MarketStats? Stats => Volatile.Read(ref _stats);
    public long TradesSeen { get; private set; }
    public DateTimeOffset? LastTradeAt { get; private set; }
    public bool HistoryLoaded { get; private set; }
    public long LateTrades => _builder.LateTrades;

    public SymbolState(Symbol symbol, MarketDataOptions options)
    {
        Symbol = symbol;
        _series = new CandleSeries[Timeframes.Length];
        for (var i = 0; i < Timeframes.Length; i++) _series[i] = new CandleSeries(symbol, Timeframes[i], options.CapacityFor(Timeframes[i]));
        _builder = new CandleBuilder(symbol, Timeframe.M1, options.CandleCloseGrace);
        _aggregators = Higher.Select(t => new TimeframeAggregator(symbol, Timeframe.M1, t)).ToArray();
    }

    public CandleSeries Series(Timeframe tf) => _series[IndexOf(tf)];

    private static int IndexOf(Timeframe tf)
    {
        var i = Array.IndexOf(Timeframes, tf);
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(tf));
        return i;
    }

    /// <summary>Apply a live trade. Closed candles (all timeframes) are appended to <paramref name="closedOut"/>.</summary>
    public void OnTrade(in Trade trade, List<Candle> closedOut)
    {
        TradesSeen++;
        LastTradeAt = trade.ExchangeTime;
        var prev = _quote;
        Volatile.Write(ref _quote, new PriceQuote(Symbol, trade.Price, prev?.Bid ?? 0m, prev?.Ask ?? 0m, trade.Provider, trade.Exchange, trade.ExchangeTime, trade.ReceivedAt));

        _scratch.Clear();
        _builder.OnTrade(trade, _scratch);
        Propagate(closedOut);
        RefreshForming();
    }

    /// <summary>Price-only update (e.g. the last trade before subscription). Never counts toward volume.</summary>
    public void OnQuoteOnly(in Trade trade)
    {
        var prev = _quote;
        if (prev is not null && prev.ExchangeTime >= trade.ExchangeTime) return;
        Volatile.Write(ref _quote, new PriceQuote(Symbol, trade.Price, prev?.Bid ?? 0m, prev?.Ask ?? 0m, trade.Provider, trade.Exchange, trade.ExchangeTime, trade.ReceivedAt));
    }

    public void OnTicker(in TickerUpdate t)
    {
        var prev = _quote;
        // Ticker carries best bid/ask. Its price is the last trade, which the match stream already delivered; keep the newer.
        var newer = prev is null || t.ExchangeTime >= prev.ExchangeTime;
        Volatile.Write(ref _quote, new PriceQuote(Symbol,
            newer || prev is null ? t.LastPrice : prev.Price,
            t.BestBid, t.BestAsk, t.Provider, t.Exchange,
            newer || prev is null ? t.ExchangeTime : prev.ExchangeTime,
            newer || prev is null ? t.ReceivedAt : prev.ReceivedAt));
        Volatile.Write(ref _stats, new MarketStats(t.Open24h, t.High24h, t.Low24h, t.Volume24h, t.ExchangeTime));
    }

    public void OnClock(DateTimeOffset now, List<Candle> closedOut)
    {
        _scratch.Clear();
        _builder.OnClock(now, _scratch);
        if (_scratch.Count == 0) return;
        Propagate(closedOut);
        RefreshForming();
    }

    /// <summary>
    /// Merge REST history into a series. Historical bars win for buckets up to and including the first live-built bar
    /// (which may have started mid-bucket); live bars win afterwards.
    /// </summary>
    public void ApplyHistory(Timeframe tf, IReadOnlyList<Candle> history)
    {
        var series = Series(tf);
        var forming = tf == Timeframe.M1 ? _builder.Forming() : _aggregators[Array.IndexOf(Higher, tf)].Forming();
        var existing = series.Snapshot().Closed;
        DateTimeOffset? firstLive = null;
        foreach (var c in existing) if (c.Source == CandleSource.Live) { firstLive = c.OpenTime; break; }

        var map = new SortedDictionary<DateTimeOffset, Candle>();
        foreach (var h in history)
        {
            if (h.Timeframe != tf) throw new ArgumentException($"Expected {tf} history, got {h.Timeframe}.");
            if (forming is { } f && h.OpenTime >= f.OpenTime) continue;
            map[h.OpenTime] = h;
        }
        foreach (var e in existing)
        {
            if (e.Source == CandleSource.Live && firstLive is { } fl && e.OpenTime > fl) map[e.OpenTime] = e;
            else map.TryAdd(e.OpenTime, e);
        }
        series.Reset(map.Values.ToList());

        if (tf == Timeframe.M1 && series.Last is { } last) _builder.Seed(last.OpenTime, last.Close);
        HistoryLoaded = true;
    }

    /// <summary>
    /// After provider-served history has been applied: derive timeframes the provider does not serve
    /// (3m from 1m, 30m from 15m, 4h from 1h), then re-prime every aggregator from the 1m series so their
    /// forming bars reflect history + live bars consistently.
    /// </summary>
    public void RebuildDerived(List<Candle> closedOut)
    {
        Derive(Timeframe.M1, Timeframe.M3);
        Derive(Timeframe.M15, Timeframe.M30);
        Derive(Timeframe.H1, Timeframe.H4);

        var m1 = Series(Timeframe.M1).Snapshot().Closed;
        for (var i = 0; i < _aggregators.Length; i++)
        {
            var agg = _aggregators[i];
            var target = Series(agg.Target);
            agg.Reset();
            var resumeFrom = target.Last is { } last ? last.OpenTime + agg.Target.Duration() : DateTimeOffset.MinValue;
            _scratch.Clear();
            foreach (var c in m1)
            {
                if (c.OpenTime < resumeFrom) continue;
                agg.OnBaseCandleClosed(c, _scratch);
            }
            foreach (var c in _scratch)
            {
                target.Append(c);
                closedOut.Add(c);
            }
        }
        RefreshForming();
    }

    private void Derive(Timeframe source, Timeframe target)
    {
        var src = Series(source).Snapshot().Closed;
        if (src.Length == 0) return;
        var derived = TimeframeAggregator.AggregateAll(Symbol, source, target, src)
            .Where(c => c.Source != CandleSource.Live)
            .ToList();
        if (derived.Count > 0) ApplyHistory(target, derived);
    }

    private void Propagate(List<Candle> closedOut)
    {
        // _scratch holds newly closed 1m candles; feed them to the higher timeframes.
        var m1Closed = _scratch.ToArray();
        _scratch.Clear();
        foreach (var c in m1Closed)
        {
            _series[0].Append(c);
            closedOut.Add(c);
            for (var i = 0; i < _aggregators.Length; i++)
            {
                _aggregators[i].OnBaseCandleClosed(c, _scratch);
            }
            if (_scratch.Count > 0)
            {
                foreach (var hc in _scratch)
                {
                    Series(hc.Timeframe).Append(hc);
                    closedOut.Add(hc);
                }
                _scratch.Clear();
            }
        }
    }

    private void RefreshForming()
    {
        var m1 = _builder.Forming();
        _series[0].SetForming(m1);
        for (var i = 0; i < _aggregators.Length; i++) Series(_aggregators[i].Target).SetForming(_aggregators[i].FormingWith(m1));
    }
}
