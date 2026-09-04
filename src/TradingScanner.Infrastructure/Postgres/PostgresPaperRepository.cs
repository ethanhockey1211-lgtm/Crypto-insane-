using Npgsql;
using NpgsqlTypes;
using TradingScanner.Signals.Paper;

namespace TradingScanner.Infrastructure.Postgres;

public sealed class PostgresPaperRepository : IPaperRepository
{
    private readonly NpgsqlDataSource _db;
    public PostgresPaperRepository(NpgsqlDataSource db) => _db = db;

    public async Task<PaperAccount?> GetAccountAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select data from paper_accounts order by created_at desc limit 1");
        return (await PostgresAlertRepository.ReadAll<PaperAccount>(cmd, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task SaveAccountAsync(PaperAccount account, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("insert into paper_accounts (id, created_at, data) values ($1, $2, $3) on conflict (id) do update set data = excluded.data");
        cmd.Parameters.AddWithValue(account.Id);
        cmd.Parameters.AddWithValue(account.CreatedAt);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Json.Write(account), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveOrderAsync(PaperOrder order, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("insert into paper_orders (id, account_id, symbol, status, created_at, data) values ($1, $2, $3, $4, $5, $6) on conflict (id) do update set status = excluded.status, data = excluded.data");
        cmd.Parameters.AddWithValue(order.Id);
        cmd.Parameters.AddWithValue(order.AccountId);
        cmd.Parameters.AddWithValue(order.Symbol);
        cmd.Parameters.AddWithValue(order.Status.ToString());
        cmd.Parameters.AddWithValue(order.CreatedAt);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Json.Write(order), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SavePositionAsync(PaperPosition position, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("insert into paper_positions (id, account_id, symbol, status, opened_at, data) values ($1, $2, $3, $4, $5, $6) on conflict (id) do update set status = excluded.status, data = excluded.data");
        cmd.Parameters.AddWithValue(position.Id);
        cmd.Parameters.AddWithValue(position.AccountId);
        cmd.Parameters.AddWithValue(position.Symbol);
        cmd.Parameters.AddWithValue(position.Status.ToString());
        cmd.Parameters.AddWithValue(position.OpenedAt);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Json.Write(position), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PaperOrder>> ListOrdersAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select data from paper_orders order by created_at desc limit 5000");
        return await PostgresAlertRepository.ReadAll<PaperOrder>(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PaperPosition>> ListPositionsAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select data from paper_positions order by opened_at desc limit 5000");
        return await PostgresAlertRepository.ReadAll<PaperPosition>(cmd, ct).ConfigureAwait(false);
    }
}
