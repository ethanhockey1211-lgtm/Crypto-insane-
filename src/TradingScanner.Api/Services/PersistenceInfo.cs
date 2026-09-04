namespace TradingScanner.Api.Services;

/// <summary>Which store backs alerts, paper trades, signals and candle archives. Reported on the status endpoint.</summary>
public sealed record PersistenceInfo(string Kind)
{
    public static readonly PersistenceInfo Memory = new("memory");
    public static readonly PersistenceInfo Postgres = new("postgres");
}
