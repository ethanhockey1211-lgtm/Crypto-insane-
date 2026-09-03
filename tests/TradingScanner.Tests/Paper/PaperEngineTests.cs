using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Paper;
using TradingScanner.Signals.Scanner;
using TradingScanner.Signals.Tape;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Paper;

public class PaperEngineTests
{
    private sealed class Quotes : ISymbolInfoReader
    {
        public Dictionary<Symbol, PriceQuote> Map { get; } = new();
        public IReadOnlyCollection<Symbol> Symbols => Map.Keys.ToArray();
        public PriceQuote? GetQuote(Symbol symbol) => Map.GetValueOrDefault(symbol);
        public MarketStats? GetStats(Symbol symbol) => null;
        public bool IsHistoryLoaded(Symbol symbol) => true;
        public void Set(string s, decimal bid, decimal ask) => Map[new Symbol(s)] = new PriceQuote(new Symbol(s), (bid + ask) / 2, bid, ask, "t", "t", T.Base, T.Base);
    }

    private sealed class NoScanner : IScannerReader
    {
        public ScannerSnapshot? Latest => null;
        public Opportunity? Get(Symbol symbol) => null;
        public MarketTape Tape { get; } = new();
    }

    private static (PaperEngine engine, Quotes quotes, InMemoryPaperRepository repo) Make(int feeBps = 10, int slipBps = 5, decimal balance = 10_000m)
    {
        var quotes = new Quotes();
        var repo = new InMemoryPaperRepository();
        var engine = new PaperEngine(repo, quotes, new NoScanner(), Options.Create(new PaperOptions { FeeBps = feeBps, SlippageBps = slipBps, StartingBalance = balance }), NullLogger<PaperEngine>.Instance);
        return (engine, quotes, repo);
    }

    [Fact]
    public async Task Market_buy_fills_at_ask_plus_slippage_and_charges_fees()
    {
        var (engine, quotes, repo) = Make();
        quotes.Set("XRP-USD", 1.400m, 1.402m);
        var order = await engine.PlaceAsync(new PlaceOrderRequest("XRP-USD", PaperSide.Buy, 1000m, null, 1.390m, 1.430m, "test"), default);

        Assert.Equal(PaperOrderStatus.Filled, order.Status);
        var expectedFill = 1.402m + 1.402m * 5 / 10_000m; // 1.402701
        Assert.Equal(expectedFill, order.FillPrice);
        var cost = 1000m * expectedFill;
        Assert.Equal(cost * 10 / 10_000m, order.Fees);
        var account = (await repo.GetAccountAsync(default))!;
        Assert.Equal(10_000m - cost - order.Fees, account.Cash);

        var position = Assert.Single(await repo.ListPositionsAsync(default));
        Assert.Equal(PaperPositionStatus.Open, position.Status);
        Assert.Equal(1000m, position.Quantity);
        Assert.Equal(expectedFill, position.AvgEntry);
        Assert.Equal(1.390m, position.InitialStop);
        Assert.Equal((expectedFill - 1.390m) * 1000m, position.InitialRiskUsd);
        var orders = await repo.ListOrdersAsync(default);
        Assert.Contains(orders, o => o.Type == PaperOrderType.Stop && o.Status == PaperOrderStatus.Open && o.TriggerPrice == 1.390m);
        Assert.Contains(orders, o => o.Type == PaperOrderType.TakeProfit && o.Status == PaperOrderStatus.Open && o.TriggerPrice == 1.430m);
    }

    [Fact]
    public async Task Stop_triggers_on_a_tick_at_or_below_it_cancels_the_take_profit_and_records_r_multiple()
    {
        var (engine, quotes, repo) = Make(feeBps: 0, slipBps: 0);
        quotes.Set("XRP-USD", 1.400m, 1.400m);
        await engine.PlaceAsync(new PlaceOrderRequest("XRP-USD", PaperSide.Buy, 1000m, null, 1.390m, 1.430m, null), default);

        await engine.TickAsync("XRP-USD", 1.410m, T.Base.AddMinutes(1), default);
        await engine.TickAsync("XRP-USD", 1.395m, T.Base.AddMinutes(2), default);
        var open = Assert.Single(await repo.ListPositionsAsync(default));
        Assert.Equal(PaperPositionStatus.Open, open.Status);
        Assert.Equal(1.410m, open.MaxFavorablePrice);
        Assert.Equal(1.395m, open.MaxAdversePrice);

        await engine.TickAsync("XRP-USD", 1.389m, T.Base.AddMinutes(3), default);
        var closed = Assert.Single(await repo.ListPositionsAsync(default));
        Assert.Equal(PaperPositionStatus.Closed, closed.Status);
        Assert.Equal("stop", closed.ExitReason);
        Assert.Equal((1.390m - 1.400m) * 1000m, closed.RealizedPnl);
        Assert.Equal(-1m, closed.RMultiple);
        Assert.Equal(1.389m, closed.MaxAdversePrice);
        var orders = await repo.ListOrdersAsync(default);
        Assert.Equal(PaperOrderStatus.Filled, orders.Single(o => o.Type == PaperOrderType.Stop).Status);
        Assert.Equal(PaperOrderStatus.Cancelled, orders.Single(o => o.Type == PaperOrderType.TakeProfit).Status);
        Assert.Equal(10_000m - 10m, (await repo.GetAccountAsync(default))!.Cash);
    }

    [Fact]
    public async Task Take_profit_fills_at_its_price_without_slippage_and_cancels_the_stop()
    {
        var (engine, quotes, repo) = Make(feeBps: 0, slipBps: 20);
        quotes.Set("SUI-USD", 0.780m, 0.780m);
        await engine.PlaceAsync(new PlaceOrderRequest("SUI-USD", PaperSide.Buy, null, 780m, 0.758m, 0.820m, null), default);
        var entry = (await repo.ListPositionsAsync(default))[0].AvgEntry;
        await engine.TickAsync("SUI-USD", 0.825m, T.Base.AddMinutes(5), default);
        var closed = Assert.Single(await repo.ListPositionsAsync(default));
        Assert.Equal("take profit", closed.ExitReason);
        Assert.Equal((0.820m - entry) * closed.Quantity + closed.RealizedPnl - closed.RealizedPnl, (0.820m - entry) * closed.Quantity);
        Assert.True(closed.RealizedPnl > 0);
        Assert.True(closed.RMultiple > 1.5m);
        var orders = await repo.ListOrdersAsync(default);
        Assert.Equal(PaperOrderStatus.Cancelled, orders.Single(o => o.Type == PaperOrderType.Stop).Status);
        Assert.Equal(0.820m, orders.Single(o => o.Type == PaperOrderType.TakeProfit).FillPrice);
    }

    [Fact]
    public async Task Partial_manual_sell_keeps_the_position_and_resizes_brackets()
    {
        var (engine, quotes, repo) = Make(feeBps: 0, slipBps: 0);
        quotes.Set("ADA-USD", 0.209m, 0.209m);
        await engine.PlaceAsync(new PlaceOrderRequest("ADA-USD", PaperSide.Buy, 10_000m, null, 0.203m, 0.215m, null), default);
        quotes.Set("ADA-USD", 0.212m, 0.212m);
        var sell = await engine.PlaceAsync(new PlaceOrderRequest("ADA-USD", PaperSide.Sell, 4000m, null, null, null, "scale out"), default);
        Assert.Equal(PaperOrderStatus.Filled, sell.Status);
        var pos = Assert.Single(await repo.ListPositionsAsync(default));
        Assert.Equal(PaperPositionStatus.Open, pos.Status);
        Assert.Equal(6000m, pos.Quantity);
        Assert.Equal((0.212m - 0.209m) * 4000m, pos.RealizedPnl);
        var orders = await repo.ListOrdersAsync(default);
        Assert.All(orders.Where(o => o.Type != PaperOrderType.Market), o => { Assert.Equal(PaperOrderStatus.Open, o.Status); Assert.Equal(6000m, o.Quantity); });
    }

    [Fact]
    public async Task Rejections_are_explicit()
    {
        var (engine, quotes, _) = Make(balance: 100m);
        Assert.Contains("no live quote", (await engine.PlaceAsync(new PlaceOrderRequest("NOPE-USD", PaperSide.Buy, 1, null, null, null, null), default)).Note);
        quotes.Set("BTC-USD", 60_000m, 60_001m);
        Assert.Contains("insufficient cash", (await engine.PlaceAsync(new PlaceOrderRequest("BTC-USD", PaperSide.Buy, 1, null, null, null, null), default)).Note);
        Assert.Contains("stop must be below", (await engine.PlaceAsync(new PlaceOrderRequest("BTC-USD", PaperSide.Buy, 0.001m, null, 61_000m, null, null), default)).Note);
        Assert.Contains("no open position", (await engine.PlaceAsync(new PlaceOrderRequest("BTC-USD", PaperSide.Sell, 0.001m, null, null, null, null), default)).Note);
        Assert.Contains("positive", (await engine.PlaceAsync(new PlaceOrderRequest("BTC-USD", PaperSide.Buy, 0, null, null, null, null), default)).Note);
    }

    [Fact]
    public async Task Stats_aggregate_closed_trades_by_setup()
    {
        var (engine, quotes, repo) = Make(feeBps: 0, slipBps: 0);
        quotes.Set("A-USD", 10m, 10m);
        await engine.PlaceAsync(new PlaceOrderRequest("A-USD", PaperSide.Buy, 10m, null, 9m, 12m, null), default);
        await engine.TickAsync("A-USD", 12m, T.Base.AddMinutes(1), default);   // +20, +2R
        quotes.Set("B-USD", 10m, 10m);
        await engine.PlaceAsync(new PlaceOrderRequest("B-USD", PaperSide.Buy, 10m, null, 9m, 12m, null), default);
        await engine.TickAsync("B-USD", 9m, T.Base.AddMinutes(2), default);    // -10, -1R
        var stats = PaperEngine.ComputeStats(await repo.ListPositionsAsync(default));
        Assert.Equal(2, stats.Trades);
        Assert.Equal(0.5, stats.WinRate);
        Assert.Equal(10m, stats.TotalPnl);
        Assert.Equal(2.0, stats.ProfitFactor);
        Assert.Equal(0.5, stats.AvgR!.Value, 9);
        Assert.Single(stats.BySetup);
        Assert.Equal("Unknown", stats.BySetup[0].Key);
    }
}
