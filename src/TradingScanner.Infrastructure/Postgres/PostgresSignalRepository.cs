using Npgsql;
using NpgsqlTypes;
using TradingScanner.Signals.Performance;

namespace TradingScanner.Infrastructure.Postgres;

public sealed class PostgresSignalRepository : ISignalRepository
{
    private readonly NpgsqlDataSource _db;
    public PostgresSignalRepository(NpgsqlDataSource db) => _db = db;

    public async Task SaveAsync(SignalRecord signal, SignalOutcome outcome, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("insert into signals (id, symbol, at, setup, score, complete, config_version, signal, outcome) values ($1, $2, $3, $4, $5, $6, $7, $8, $9) on conflict (id) do update set complete = excluded.complete, outcome = excluded.outcome");
        cmd.Parameters.AddWithValue(signal.Id);
        cmd.Parameters.AddWithValue(signal.Symbol);
        cmd.Parameters.AddWithValue(signal.At);
        cmd.Parameters.AddWithValue(signal.Setup);
        cmd.Parameters.AddWithValue(signal.Score);
        cmd.Parameters.AddWithValue(outcome.Complete);
        cmd.Parameters.AddWithValue(signal.ConfigVersion);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Json.Write(signal), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = Json.Write(outcome), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SignalWithOutcome>> ListAsync(int limit, string? symbol, CancellationToken ct)
    {
        await using var cmd = symbol is null
            ? _db.CreateCommand("select signal, outcome from signals order by at desc limit $1")
            : _db.CreateCommand("select signal, outcome from signals where symbol = $2 order by at desc limit $1");
        cmd.Parameters.AddWithValue(limit);
        if (symbol is not null) cmd.Parameters.AddWithValue(symbol);
        return await ReadPairs(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SignalWithOutcome>> ListIncompleteAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select signal, outcome from signals where complete = false order by at");
        return await ReadPairs(cmd, ct).ConfigureAwait(false);
    }

    private static async Task<List<SignalWithOutcome>> ReadPairs(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<SignalWithOutcome>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new SignalWithOutcome(Json.Read<SignalRecord>(reader.GetString(0)), Json.Read<SignalOutcome>(reader.GetString(1))));
        return list;
    }
}
