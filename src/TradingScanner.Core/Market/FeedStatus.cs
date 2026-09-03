namespace TradingScanner.Core.Market;

public enum FeedStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    /// <summary>Connected but data gaps were detected or heartbeats are late.</summary>
    Degraded = 3,
    Reconnecting = 4,
}

public sealed record FeedStatusChange(string Provider, int ConnectionIndex, FeedStatus Status, string? Reason, DateTimeOffset At);

/// <summary>Missed trades detected via trade-id continuity. Emitted so consumers can backfill instead of pretending nothing happened.</summary>
public sealed record DataGap(string Provider, Symbol Symbol, long ExpectedTradeId, long ReceivedTradeId, DateTimeOffset At)
{
    public long MissedTrades => ReceivedTradeId - ExpectedTradeId;
}
