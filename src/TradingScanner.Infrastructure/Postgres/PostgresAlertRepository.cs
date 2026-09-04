using Npgsql;
using NpgsqlTypes;
using TradingScanner.Signals.Alerts;

namespace TradingScanner.Infrastructure.Postgres;

public sealed class PostgresAlertRepository : IAlertRepository
{
    private readonly NpgsqlDataSource _db;
    public PostgresAlertRepository(NpgsqlDataSource db) => _db = db;

    public async Task<IReadOnlyList<AlertRule>> ListRulesAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select data from alert_rules order by created_at");
        return await ReadAll<AlertRule>(cmd, ct).ConfigureAwait(false);
    }

    public async Task<AlertRule?> GetRuleAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select data from alert_rules where id = $1");
        cmd.Parameters.AddWithValue(id);
        return (await ReadAll<AlertRule>(cmd, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task UpsertRuleAsync(AlertRule rule, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("insert into alert_rules (id, name, enabled, symbol, created_at, data) values ($1, $2, $3, $4, $5, $6) on conflict (id) do update set name = excluded.name, enabled = excluded.enabled, symbol = excluded.symbol, data = excluded.data");
        cmd.Parameters.AddWithValue(rule.Id);
        cmd.Parameters.AddWithValue(rule.Name);
        cmd.Parameters.AddWithValue(rule.Enabled);
        cmd.Parameters.AddWithValue(rule.Symbol is null ? DBNull.Value : rule.Symbol);
        cmd.Parameters.AddWithValue(rule.CreatedAt);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Json.Write(rule), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("delete from alert_rules where id = $1");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task AppendEventAsync(AlertEvent evt, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("insert into alert_events (id, rule_id, at, symbol, data) values ($1, $2, $3, $4, $5) on conflict (id) do nothing");
        cmd.Parameters.AddWithValue(evt.Id);
        cmd.Parameters.AddWithValue(evt.RuleId);
        cmd.Parameters.AddWithValue(evt.At);
        cmd.Parameters.AddWithValue(evt.Symbol);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Json.Write(evt), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlertEvent>> ListEventsAsync(int limit, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("select data from alert_events order by at desc limit $1");
        cmd.Parameters.AddWithValue(limit);
        return await ReadAll<AlertEvent>(cmd, ct).ConfigureAwait(false);
    }

    internal static async Task<List<T>> ReadAll<T>(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(Json.Read<T>(reader.GetString(0)));
        return list;
    }
}
