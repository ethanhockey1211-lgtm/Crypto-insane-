using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingScanner.Core.Market;
using TradingScanner.Core.Providers;

namespace TradingScanner.Infrastructure.Postgres;

/// <summary>Persists closed candles (all timeframes) in batches, off the engine thread. Idempotent on replay.</summary>
public sealed class CandleArchiver : BackgroundService, IMarketEventObserver
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<CandleArchiver> _logger;
    private readonly Channel<Candle> _queue = Channel.CreateBounded<Candle>(new BoundedChannelOptions(50_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private long _written;

    public CandleArchiver(NpgsqlDataSource db, ILogger<CandleArchiver> logger)
    {
        _db = db;
        _logger = logger;
    }

    public long Written => Interlocked.Read(ref _written);

    public void OnCandleClosed(in Candle candle) => _queue.Writer.TryWrite(candle);
    public void OnQuote(PriceQuote quote) { }
    public void OnFeedStatus(FeedStatusChange change) { }
    public void OnGap(DataGap gap) { }
    public void OnHistoryApplied(Symbol symbol) { }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Candle>(512);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 512 && _queue.Reader.TryRead(out var c)) batch.Add(c);
                if (batch.Count == 0) continue;
                try { await WriteAsync(batch, stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Candle archive batch of {Count} failed", batch.Count); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task WriteAsync(IReadOnlyList<Candle> candles, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(conn);
        foreach (var c in candles)
        {
            var cmd = new NpgsqlBatchCommand("insert into candles (symbol, timeframe, open_time, open, high, low, close, volume, quote_volume, buy_volume, sell_volume, trade_count, source) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) on conflict (symbol, timeframe, open_time) do update set open = excluded.open, high = excluded.high, low = excluded.low, close = excluded.close, volume = excluded.volume, quote_volume = excluded.quote_volume, buy_volume = excluded.buy_volume, sell_volume = excluded.sell_volume, trade_count = excluded.trade_count, source = excluded.source");
            cmd.Parameters.AddWithValue(c.Symbol.Value);
            cmd.Parameters.AddWithValue((int)c.Timeframe);
            cmd.Parameters.AddWithValue(c.OpenTime);
            cmd.Parameters.AddWithValue(c.Open); cmd.Parameters.AddWithValue(c.High); cmd.Parameters.AddWithValue(c.Low); cmd.Parameters.AddWithValue(c.Close);
            cmd.Parameters.AddWithValue(c.Volume); cmd.Parameters.AddWithValue(c.QuoteVolume); cmd.Parameters.AddWithValue(c.BuyVolume); cmd.Parameters.AddWithValue(c.SellVolume);
            cmd.Parameters.AddWithValue(c.TradeCount);
            cmd.Parameters.AddWithValue((short)c.Source);
            batch.BatchCommands.Add(cmd);
        }
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        Interlocked.Add(ref _written, candles.Count);
    }

    /// <summary>Read archived closed candles, ascending.</summary>
    public async Task<List<Candle>> ReadAsync(Symbol symbol, Timeframe tf, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select open_time, open, high, low, close, volume, quote_volume, buy_volume, sell_volume, trade_count, source from candles where symbol = $1 and timeframe = $2 and open_time >= $3 and open_time < $4 order by open_time");
        cmd.Parameters.AddWithValue(symbol.Value); cmd.Parameters.AddWithValue((int)tf); cmd.Parameters.AddWithValue(from); cmd.Parameters.AddWithValue(to);
        var list = new List<Candle>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new Candle(symbol, tf, reader.GetFieldValue<DateTimeOffset>(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetInt32(9), (CandleSource)reader.GetInt16(10)));
        return list;
    }
}
