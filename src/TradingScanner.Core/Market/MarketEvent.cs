namespace TradingScanner.Core.Market;

public enum MarketEventKind : byte
{
    Trade = 0,
    Ticker = 1,
    Status = 2,
    Gap = 3,
    /// <summary>Periodic wall-clock tick injected by the engine so candles close even when no trades arrive.</summary>
    ClockTick = 4,
    /// <summary>A quote-only update (e.g. the "last_match" Coinbase sends on subscribe) that must NOT count toward volume.</summary>
    QuoteOnly = 5,
    /// <summary>Work to run on the engine thread (history merge). Rare.</summary>
    Command = 6,
}

/// <summary>
/// Tagged union carried through the ingestion channel. A struct so the hot path does not allocate per message.
/// Reference-typed payloads (Status, Gap) are rare.
/// </summary>
public readonly struct MarketEvent
{
    public MarketEventKind Kind { get; }
    public Trade Trade { get; }
    public TickerUpdate Ticker { get; }
    public FeedStatusChange? Status { get; }
    public DataGap? Gap { get; }
    public Action? Command { get; }
    public DateTimeOffset At { get; }

    private MarketEvent(MarketEventKind kind, Trade trade, TickerUpdate ticker, FeedStatusChange? status, DataGap? gap, DateTimeOffset at, Action? command = null)
    {
        Kind = kind; Trade = trade; Ticker = ticker; Status = status; Gap = gap; At = at; Command = command;
    }

    public static MarketEvent FromTrade(in Trade t) => new(MarketEventKind.Trade, t, default, null, null, t.ReceivedAt);
    public static MarketEvent FromQuoteOnly(in Trade t) => new(MarketEventKind.QuoteOnly, t, default, null, null, t.ReceivedAt);
    public static MarketEvent FromTicker(in TickerUpdate t) => new(MarketEventKind.Ticker, default, t, null, null, t.ReceivedAt);
    public static MarketEvent FromStatus(FeedStatusChange s) => new(MarketEventKind.Status, default, default, s, null, s.At);
    public static MarketEvent FromGap(DataGap g) => new(MarketEventKind.Gap, default, default, null, g, g.At);
    public static MarketEvent Clock(DateTimeOffset at) => new(MarketEventKind.ClockTick, default, default, null, null, at);
    public static MarketEvent FromCommand(Action command) => new(MarketEventKind.Command, default, default, null, null, default, command);
}
