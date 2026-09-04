using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TradingScanner.Infrastructure.Postgres;

/// <summary>Applies embedded SQL migrations in name order, once each, recorded in schema_migrations.</summary>
public sealed class Migrator
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<Migrator> _logger;

    public Migrator(NpgsqlDataSource db, ILogger<Migrator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var create = new NpgsqlCommand("create table if not exists schema_migrations (name text primary key, applied_at timestamptz not null default now())", conn))
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var applied = new HashSet<string>();
        await using (var read = new NpgsqlCommand("select name from schema_migrations", conn))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) applied.Add(reader.GetString(0));

        var assembly = typeof(Migrator).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var name = resource[(resource.LastIndexOf("Migrations.", StringComparison.Ordinal) + "Migrations.".Length)..];
            if (applied.Contains(name)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var sr = new StreamReader(stream);
            var sql = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using (var rec = new NpgsqlCommand("insert into schema_migrations (name) values ($1)", conn, tx)) { rec.Parameters.AddWithValue(name); await rec.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Applied migration {Name}", name);
        }
    }
}
