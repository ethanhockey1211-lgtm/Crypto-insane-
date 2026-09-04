using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingScanner.Core.Market;
using TradingScanner.Infrastructure.Postgres;
using TradingScanner.Signals.Alerts;
using TradingScanner.Signals.Paper;
using TradingScanner.Signals.Performance;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Infrastructure;

/// <summary>
/// Real-database round trips. Run only when TS_TEST_POSTGRES holds a connection string to a disposable database;
/// they migrate it and write to it. Skipped otherwise so the suite stays green on machines without Postgres.
/// </summary>
public sealed class PostgresRepositoryTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TS_TEST_POSTGRES");
    private NpgsqlDataSource? _db;

    public async Task InitializeAsync()
    {
        if (ConnectionString is null) return;
        _db = NpgsqlDataSource.Create(ConnectionString);
        await using (var cmd = _db.CreateCommand("drop table if exists schema_migrations, alert_rules, alert_events, paper_accounts, paper_orders, paper_positions, signals, candles cascade"))
            await cmd.ExecuteNonQueryAsync();
        await new Migrator(_db, NullLogger<Migrator>.Instance).MigrateAsync(default);
    }

    public async Task DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }

    private static bool Skip => ConnectionString is null;

    [SkippableFact]
    public async Task Migrations_are_idempotent_and_recorded()
    {
        Xunit.Skip.If(Skip, "TS_TEST_POSTGRES not set");
        await new Migrator(_db!, NullLogger<Migrator>.Instance).MigrateAsync(default); // second run: nothing to do
        await using var cmd = _db!.CreateCommand("select count(*) from schema_migrations");
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Alert_rules_and_events_round_trip()
    {
        Xunit.Skip.If(Skip, "TS_TEST_POSTGRES not set");
        var repo = new PostgresAlertRepository(_db!);
        var rule = new AlertRule(Guid.NewGuid(), "XRP breakout", true, "XRP-USD", [new AlertCondition(AlertField.Price, AlertOperator.Gt, "1.405"), new AlertCondition(AlertField.RelVol, AlertOperator.Gte, "1.5")], 180, 600, ["browser", "webhook"], "https://example.invalid/hook", T.Base, null, true);
        await repo.UpsertRuleAsync(rule, default);
        var loaded = await repo.GetRuleAsync(rule.Id, default);
        Assert.NotNull(loaded);
        Assert.Equal(rule with { Conditions = [], Channels = [] }, loaded with { Conditions = [], Channels = [] });
        Assert.Equal(rule.Conditions, loaded.Conditions);
        Assert.Equal(rule.Channels, loaded.Channels);
        await repo.UpsertRuleAsync(rule with { Enabled = false, LastFiredAt = T.Base.AddMinutes(5) }, default);
        Assert.False((await repo.ListRulesAsync(default)).Single().Enabled);

        var evt = new AlertEvent(Guid.NewGuid(), rule.Id, rule.Name, T.Base.AddMinutes(5), "XRP-USD", "fired", new Dictionary<string, string> { ["Price"] = "1.41" });
        await repo.AppendEventAsync(evt, default);
        await repo.AppendEventAsync(evt, default); // idempotent
        var events = await repo.ListEventsAsync(10, default);
        Assert.Single(events);
        Assert.Equal(evt.Message, events[0].Message);
        Assert.Equal("1.41", events[0].Values["Price"]);
        Assert.True(await repo.DeleteRuleAsync(rule.Id, default));
        Assert.False(await repo.DeleteRuleAsync(rule.Id, default));
    }

    [SkippableFact]
    public async Task Paper_account_orders_and_positions_round_trip()
    {
        Xunit.Skip.If(Skip, "TS_TEST_POSTGRES not set");
        var repo = new PostgresPaperRepository(_db!);
        Assert.Null(await repo.GetAccountAsync(default));
        var account = new PaperAccount(Guid.NewGuid(), "Paper", 10_000m, 9_500.123456m, 10, 5, T.Base);
        await repo.SaveAccountAsync(account, default);
        Assert.Equal(account, await repo.GetAccountAsync(default));

        var position = new PaperPosition(Guid.NewGuid(), account.Id, "XRP-USD", PaperPositionStatus.Open, 1000m, 1.4027m, T.Base, null, 0, 1.4m, 1.41m, 1.39m, 1.39m, 12.7m, 88, "Breakout", "RiskOn", null, null, null, null);
        await repo.SavePositionAsync(position, default);
        var order = new PaperOrder(Guid.NewGuid(), account.Id, "XRP-USD", PaperSide.Sell, PaperOrderType.Stop, 1000m, 1.39m, PaperOrderStatus.Open, T.Base, null, null, 0, 0, position.Id, "bracket stop");
        await repo.SaveOrderAsync(order, default);
        await repo.SaveOrderAsync(order with { Status = PaperOrderStatus.Filled, FilledAt = T.Base.AddMinutes(1), FillPrice = 1.3893m }, default);

        var orders = await repo.ListOrdersAsync(default);
        Assert.Single(orders);
        Assert.Equal(PaperOrderStatus.Filled, orders[0].Status);
        Assert.Equal(1.3893m, orders[0].FillPrice);
        var positions = await repo.ListPositionsAsync(default);
        Assert.Equal(position, positions.Single());
    }

    [SkippableFact]
    public async Task Signals_upsert_outcomes_and_filter_incomplete()
    {
        Xunit.Skip.If(Skip, "TS_TEST_POSTGRES not set");
        var repo = new PostgresSignalRepository(_db!);
        var signal = new SignalRecord(Guid.NewGuid(), "SOL-USD", T.Base, "Breakout", "High", 84, new Dictionary<string, double> { ["Momentum"] = 15.5 }, 2, 150.25, 150.3, 148.9, 153, 155, 158, 1.93, "RiskOn", "Bullish", false, 1);
        var outcome = new SignalOutcome(signal.Id, null, null, null, null, null, 0, 0, false, false, false, false, "none", null, T.Base, false);
        await repo.SaveAsync(signal, outcome, default);
        Assert.Single(await repo.ListIncompleteAsync(default));

        var done = outcome with { Ret5m = 0.004, Ret1h = 0.012, Mfe = 0.02, Mae = -0.003, Target1Hit = true, FirstEvent = "t1", R = 1.93, Complete = true, LastPrice = T.Base.AddHours(1) };
        await repo.SaveAsync(signal, done, default);
        Assert.Empty(await repo.ListIncompleteAsync(default));
        var listed = await repo.ListAsync(10, "SOL-USD", default);
        var item = Assert.Single(listed);
        var none = new Dictionary<string, double>();
        Assert.Equal(signal with { Components = none }, item.Signal with { Components = none });
        Assert.Equal(signal.Components, item.Signal.Components);
        Assert.Equal(done, item.Outcome);
        Assert.Empty(await repo.ListAsync(10, "BTC-USD", default));
    }

    [SkippableFact]
    public async Task Candle_archive_is_idempotent_and_reads_back_ascending()
    {
        Xunit.Skip.If(Skip, "TS_TEST_POSTGRES not set");
        var archiver = new CandleArchiver(_db!, NullLogger<CandleArchiver>.Instance);
        var sym = new Symbol("ETH-USD");
        var candles = Enumerable.Range(0, 5).Select(i => T.Candle("ETH-USD", Timeframe.M1, T.Base.AddMinutes(i), 100 + i, 101 + i, 99 + i, 100.5m + i, 3)).ToList();
        await archiver.WriteAsync(candles, default);
        await archiver.WriteAsync(candles, default);
        var read = await archiver.ReadAsync(sym, Timeframe.M1, T.Base, T.Base.AddMinutes(10), default);
        Assert.Equal(5, read.Count);
        Assert.Equal(candles.Select(c => c.Close), read.Select(c => c.Close));
        Assert.Equal(CandleSource.Live, read[0].Source);
        Assert.Equal(10, archiver.Written);
    }
}

public class ConnectionStringTests
{
    [Fact]
    public void Postgres_urls_are_converted_for_npgsql_and_key_value_strings_pass_through()
    {
        var cs = TradingScanner.Infrastructure.ServiceCollectionExtensions.NormalizeConnectionString("postgres://scanner:p%40ss@db.internal:5432/tradingscanner");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("db.internal", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("scanner", b.Username);
        Assert.Equal("p@ss", b.Password);
        Assert.Equal("tradingscanner", b.Database);
        Assert.Equal(Npgsql.SslMode.Require, b.SslMode);

        var local = TradingScanner.Infrastructure.ServiceCollectionExtensions.NormalizeConnectionString("postgresql://u:p@localhost/x?sslmode=disable");
        Assert.Equal(Npgsql.SslMode.Disable, new Npgsql.NpgsqlConnectionStringBuilder(local).SslMode);

        const string kv = "Host=localhost;Username=u;Password=p;Database=x";
        Assert.Equal(kv, TradingScanner.Infrastructure.ServiceCollectionExtensions.NormalizeConnectionString(kv));
    }
}
