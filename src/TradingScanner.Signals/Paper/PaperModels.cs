namespace TradingScanner.Signals.Paper;

public enum PaperSide : byte { Buy = 0, Sell = 1 }
public enum PaperOrderType : byte { Market = 0, Stop = 1, TakeProfit = 2 }
public enum PaperOrderStatus : byte { Open = 0, Filled = 1, Cancelled = 2, Rejected = 3 }
public enum PaperPositionStatus : byte { Open = 0, Closed = 1 }

public sealed record PaperAccount(Guid Id, string Name, decimal StartingBalance, decimal Cash, int FeeBps, int SlippageBps, DateTimeOffset CreatedAt);

public sealed record PaperOrder(
    Guid Id,
    Guid AccountId,
    string Symbol,
    PaperSide Side,
    PaperOrderType Type,
    decimal Quantity,
    /// <summary>Trigger price for Stop / TakeProfit orders.</summary>
    decimal? TriggerPrice,
    PaperOrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FilledAt,
    decimal? FillPrice,
    decimal Fees,
    decimal Slippage,
    Guid? PositionId,
    string? Note);

public sealed record PaperPosition(
    Guid Id,
    Guid AccountId,
    string Symbol,
    PaperPositionStatus Status,
    decimal Quantity,
    decimal AvgEntry,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal RealizedPnl,
    decimal Fees,
    /// <summary>Best price seen while open.</summary>
    decimal MaxFavorablePrice,
    /// <summary>Worst price seen while open.</summary>
    decimal MaxAdversePrice,
    decimal? InitialStop,
    decimal? InitialRiskUsd,
    double? ScoreAtEntry,
    string? SetupAtEntry,
    string? RegimeAtEntry,
    Guid? SignalId,
    Guid? StopOrderId,
    Guid? TakeProfitOrderId,
    string? ExitReason)
{
    public decimal MfePct => AvgEntry > 0 ? (MaxFavorablePrice - AvgEntry) / AvgEntry : 0;
    public decimal MaePct => AvgEntry > 0 ? (MaxAdversePrice - AvgEntry) / AvgEntry : 0;
    /// <summary>Realized P/L in units of initial risk (null without an initial stop).</summary>
    public decimal? RMultiple => InitialRiskUsd is { } r && r > 0 ? RealizedPnl / r : null;
}

public sealed record PlaceOrderRequest(
    string Symbol,
    PaperSide Side,
    decimal? Quantity,
    /// <summary>Alternative to quantity: notional in quote currency at the current price.</summary>
    decimal? Notional,
    decimal? StopPrice,
    decimal? TakeProfitPrice,
    string? Note);

public sealed record PaperFill(PaperOrder Order, PaperPosition Position, PaperAccount Account);

public sealed record PaperStats(
    int Trades,
    int Wins,
    double WinRate,
    decimal TotalPnl,
    decimal GrossProfit,
    decimal GrossLoss,
    double? ProfitFactor,
    decimal AvgPnl,
    double? AvgR,
    double? Expectancy,
    IReadOnlyList<PaperBucket> BySetup,
    IReadOnlyList<PaperBucket> ByRegime);

public sealed record PaperBucket(string Key, int Trades, double WinRate, decimal TotalPnl, double? AvgR);
