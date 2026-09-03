using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Signals.Paper;

public sealed class PaperOptions
{
    public string AccountName { get; set; } = "Paper";
    public decimal StartingBalance { get; set; } = 10_000m;
    public int FeeBps { get; set; } = 10;
    public int SlippageBps { get; set; } = 5;
}

/// <summary>
/// Simulated execution. Market orders fill immediately against the latest quote (ask for buys, bid for sells,
/// last price when no book) plus slippage and fees. Stops and take-profits are evaluated on every price tick the
/// engine receives (scanner cycle, ~1 s) and fill at their trigger price with slippage on stops. Long only.
/// No real-money path exists anywhere in this codebase.
/// </summary>
public sealed class PaperEngine
{
    private readonly IPaperRepository _repo;
    private readonly ISymbolInfoReader _info;
    private readonly IScannerReader _scanner;
    private readonly PaperOptions _o;
    private readonly ILogger<PaperEngine> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PaperEngine(IPaperRepository repo, ISymbolInfoReader info, IScannerReader scanner, IOptions<PaperOptions> options, ILogger<PaperEngine> logger, TimeProvider? time = null)
    {
        _repo = repo;
        _info = info;
        _scanner = scanner;
        _o = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public event Action<PaperFill>? Filled;

    public async Task<PaperAccount> GetOrCreateAccountAsync(CancellationToken ct)
    {
        var a = await _repo.GetAccountAsync(ct).ConfigureAwait(false);
        if (a is not null) return a;
        a = new PaperAccount(Guid.NewGuid(), _o.AccountName, _o.StartingBalance, _o.StartingBalance, _o.FeeBps, _o.SlippageBps, _time.GetUtcNow());
        await _repo.SaveAccountAsync(a, ct).ConfigureAwait(false);
        return a;
    }

    public async Task<PaperAccount> ResetAccountAsync(decimal startingBalance, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var o in await _repo.ListOrdersAsync(ct).ConfigureAwait(false))
                if (o.Status == PaperOrderStatus.Open) await _repo.SaveOrderAsync(o with { Status = PaperOrderStatus.Cancelled }, ct).ConfigureAwait(false);
            var a = new PaperAccount(Guid.NewGuid(), _o.AccountName, startingBalance, startingBalance, _o.FeeBps, _o.SlippageBps, _time.GetUtcNow());
            await _repo.SaveAccountAsync(a, ct).ConfigureAwait(false);
            return a;
        }
        finally { _gate.Release(); }
    }

    public async Task<PaperOrder> PlaceAsync(PlaceOrderRequest req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = await GetOrCreateAccountAsync(ct).ConfigureAwait(false);
            var symbol = req.Symbol.Trim().ToUpperInvariant();
            var quote = _info.GetQuote(new Symbol(symbol));
            var now = _time.GetUtcNow();
            if (quote is null) return await RejectAsync(account, symbol, req, now, "no live quote for symbol", ct).ConfigureAwait(false);

            var side = req.Side;
            var raw = side == PaperSide.Buy ? (quote.Ask > 0 ? quote.Ask : quote.Price) : (quote.Bid > 0 ? quote.Bid : quote.Price);
            var slip = raw * account.SlippageBps / 10_000m;
            var fill = side == PaperSide.Buy ? raw + slip : raw - slip;
            var qty = req.Quantity ?? (req.Notional is { } n && fill > 0 ? n / fill : 0m);
            if (qty <= 0) return await RejectAsync(account, symbol, req, now, "quantity or notional must be positive", ct).ConfigureAwait(false);
            qty = decimal.Round(qty, 8);

            var positions = await _repo.ListPositionsAsync(ct).ConfigureAwait(false);
            var open = positions.FirstOrDefault(p => p.Status == PaperPositionStatus.Open && p.Symbol == symbol);

            if (side == PaperSide.Buy)
            {
                if (req.StopPrice is { } sp && sp >= fill) return await RejectAsync(account, symbol, req, now, "stop must be below the entry price", ct).ConfigureAwait(false);
                if (req.TakeProfitPrice is { } tp && tp <= fill) return await RejectAsync(account, symbol, req, now, "take profit must be above the entry price", ct).ConfigureAwait(false);
                var cost = qty * fill;
                var fee = cost * account.FeeBps / 10_000m;
                if (cost + fee > account.Cash) return await RejectAsync(account, symbol, req, now, $"insufficient cash: need {cost + fee:F2}, have {account.Cash:F2}", ct).ConfigureAwait(false);

                var opp = _scanner.Get(new Symbol(symbol));
                var regime = _scanner.Latest?.Market.Regime.ToString();
                PaperPosition position;
                if (open is null)
                {
                    position = new PaperPosition(Guid.NewGuid(), account.Id, symbol, PaperPositionStatus.Open, qty, fill, now, null, 0, fee, fill, fill,
                        req.StopPrice, req.StopPrice is { } s0 ? (fill - s0) * qty : null, opp?.Score, opp?.Setup.Type.ToString(), regime, null, null, null, null);
                }
                else
                {
                    var newQty = open.Quantity + qty;
                    var avg = (open.AvgEntry * open.Quantity + fill * qty) / newQty;
                    position = open with { Quantity = newQty, AvgEntry = avg, Fees = open.Fees + fee };
                }
                var order = new PaperOrder(Guid.NewGuid(), account.Id, symbol, side, PaperOrderType.Market, qty, null, PaperOrderStatus.Filled, now, now, fill, fee, slip * qty, position.Id, req.Note);
                account = account with { Cash = account.Cash - cost - fee };

                if (req.StopPrice is { } stop)
                {
                    var so = new PaperOrder(Guid.NewGuid(), account.Id, symbol, PaperSide.Sell, PaperOrderType.Stop, position.Quantity, stop, PaperOrderStatus.Open, now, null, null, 0, 0, position.Id, "bracket stop");
                    await CancelIfOpenAsync(position.StopOrderId, ct).ConfigureAwait(false);
                    await _repo.SaveOrderAsync(so, ct).ConfigureAwait(false);
                    position = position with { StopOrderId = so.Id, InitialStop = position.InitialStop ?? stop, InitialRiskUsd = position.InitialRiskUsd ?? (position.AvgEntry - stop) * position.Quantity };
                }
                if (req.TakeProfitPrice is { } tp2)
                {
                    var to = new PaperOrder(Guid.NewGuid(), account.Id, symbol, PaperSide.Sell, PaperOrderType.TakeProfit, position.Quantity, tp2, PaperOrderStatus.Open, now, null, null, 0, 0, position.Id, "bracket take profit");
                    await CancelIfOpenAsync(position.TakeProfitOrderId, ct).ConfigureAwait(false);
                    await _repo.SaveOrderAsync(to, ct).ConfigureAwait(false);
                    position = position with { TakeProfitOrderId = to.Id };
                }
                await _repo.SavePositionAsync(position, ct).ConfigureAwait(false);
                await _repo.SaveOrderAsync(order, ct).ConfigureAwait(false);
                await _repo.SaveAccountAsync(account, ct).ConfigureAwait(false);
                Filled?.Invoke(new PaperFill(order, position, account));
                return order;
            }
            else
            {
                if (open is null || open.Quantity <= 0) return await RejectAsync(account, symbol, req, now, "no open position to sell", ct).ConfigureAwait(false);
                var sellQty = Math.Min(qty, open.Quantity);
                var (order, position, acct) = await CloseAsync(account, open, sellQty, fill, slip, now, PaperOrderType.Market, "manual sell", req.Note, ct).ConfigureAwait(false);
                Filled?.Invoke(new PaperFill(order, position, acct));
                return order;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CancelAsync(Guid orderId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var orders = await _repo.ListOrdersAsync(ct).ConfigureAwait(false);
            var o = orders.FirstOrDefault(x => x.Id == orderId);
            if (o is null || o.Status != PaperOrderStatus.Open) return false;
            await _repo.SaveOrderAsync(o with { Status = PaperOrderStatus.Cancelled }, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Price update for one symbol: updates excursions, triggers stops and take-profits.</summary>
    public async Task TickAsync(string symbol, decimal price, DateTimeOffset at, CancellationToken ct)
    {
        if (price <= 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var positions = await _repo.ListPositionsAsync(ct).ConfigureAwait(false);
            var open = positions.FirstOrDefault(p => p.Status == PaperPositionStatus.Open && p.Symbol == symbol);
            if (open is null) return;
            var updated = open with { MaxFavorablePrice = Math.Max(open.MaxFavorablePrice, price), MaxAdversePrice = Math.Min(open.MaxAdversePrice, price) };
            if (updated != open) await _repo.SavePositionAsync(updated, ct).ConfigureAwait(false);

            var orders = await _repo.ListOrdersAsync(ct).ConfigureAwait(false);
            var stop = orders.FirstOrDefault(o => o.Id == updated.StopOrderId && o.Status == PaperOrderStatus.Open);
            var tp = orders.FirstOrDefault(o => o.Id == updated.TakeProfitOrderId && o.Status == PaperOrderStatus.Open);
            var account = await GetOrCreateAccountAsync(ct).ConfigureAwait(false);

            if (stop is { TriggerPrice: { } sp } && price <= sp)
            {
                var slip = sp * account.SlippageBps / 10_000m;
                var (order, position, acct) = await CloseAsync(account, updated, updated.Quantity, sp - slip, slip, at, PaperOrderType.Stop, "stop", stop.Note, ct, stop).ConfigureAwait(false);
                if (tp is not null) await _repo.SaveOrderAsync(tp with { Status = PaperOrderStatus.Cancelled }, ct).ConfigureAwait(false);
                Filled?.Invoke(new PaperFill(order, position, acct));
            }
            else if (tp is { TriggerPrice: { } tpp } && price >= tpp)
            {
                var (order, position, acct) = await CloseAsync(account, updated, updated.Quantity, tpp, 0, at, PaperOrderType.TakeProfit, "take profit", tp.Note, ct, tp).ConfigureAwait(false);
                if (stop is not null) await _repo.SaveOrderAsync(stop with { Status = PaperOrderStatus.Cancelled }, ct).ConfigureAwait(false);
                Filled?.Invoke(new PaperFill(order, position, acct));
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<(PaperOrder order, PaperPosition position, PaperAccount account)> CloseAsync(
        PaperAccount account, PaperPosition open, decimal qty, decimal fill, decimal slipPerUnit, DateTimeOffset at, PaperOrderType type, string reason, string? note, CancellationToken ct, PaperOrder? existing = null)
    {
        var proceeds = qty * fill;
        var fee = proceeds * account.FeeBps / 10_000m;
        var pnl = (fill - open.AvgEntry) * qty - fee;
        var remaining = open.Quantity - qty;
        var position = open with
        {
            Quantity = remaining,
            RealizedPnl = open.RealizedPnl + pnl,
            Fees = open.Fees + fee,
            Status = remaining <= 0 ? PaperPositionStatus.Closed : PaperPositionStatus.Open,
            ClosedAt = remaining <= 0 ? at : null,
            ExitReason = remaining <= 0 ? reason : open.ExitReason,
        };
        var order = existing is not null
            ? existing with { Status = PaperOrderStatus.Filled, FilledAt = at, FillPrice = fill, Fees = fee, Slippage = slipPerUnit * qty, Quantity = qty }
            : new PaperOrder(Guid.NewGuid(), account.Id, open.Symbol, PaperSide.Sell, type, qty, null, PaperOrderStatus.Filled, at, at, fill, fee, slipPerUnit * qty, open.Id, note);
        account = account with { Cash = account.Cash + proceeds - fee };
        if (remaining <= 0)
        {
            // Closing the whole position cancels the other bracket leg; a partial manual sell keeps brackets for the rest.
            foreach (var id in new[] { position.StopOrderId, position.TakeProfitOrderId })
                if (id is { } oid && oid != order.Id) await CancelIfOpenAsync(oid, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var id in new[] { position.StopOrderId, position.TakeProfitOrderId })
            {
                if (id is not { } oid || oid == order.Id) continue;
                var bracket = (await _repo.ListOrdersAsync(ct).ConfigureAwait(false)).FirstOrDefault(o => o.Id == oid && o.Status == PaperOrderStatus.Open);
                if (bracket is not null) await _repo.SaveOrderAsync(bracket with { Quantity = remaining }, ct).ConfigureAwait(false);
            }
        }
        await _repo.SaveOrderAsync(order, ct).ConfigureAwait(false);
        await _repo.SavePositionAsync(position, ct).ConfigureAwait(false);
        await _repo.SaveAccountAsync(account, ct).ConfigureAwait(false);
        _logger.LogInformation("Paper {Type} {Symbol} {Qty} @ {Fill} pnl {Pnl:F2} ({Reason})", type, open.Symbol, qty, fill, pnl, reason);
        return (order, position, account);
    }

    private async Task CancelIfOpenAsync(Guid? id, CancellationToken ct)
    {
        if (id is not { } oid) return;
        var o = (await _repo.ListOrdersAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == oid);
        if (o is { Status: PaperOrderStatus.Open }) await _repo.SaveOrderAsync(o with { Status = PaperOrderStatus.Cancelled }, ct).ConfigureAwait(false);
    }

    private async Task<PaperOrder> RejectAsync(PaperAccount account, string symbol, PlaceOrderRequest req, DateTimeOffset now, string reason, CancellationToken ct)
    {
        var o = new PaperOrder(Guid.NewGuid(), account.Id, symbol, req.Side, PaperOrderType.Market, req.Quantity ?? 0, null, PaperOrderStatus.Rejected, now, null, null, 0, 0, null, reason);
        await _repo.SaveOrderAsync(o, ct).ConfigureAwait(false);
        return o;
    }

    public static PaperStats ComputeStats(IReadOnlyList<PaperPosition> positions)
    {
        var closed = positions.Where(p => p.Status == PaperPositionStatus.Closed).ToList();
        var wins = closed.Count(p => p.RealizedPnl > 0);
        var gross = closed.Where(p => p.RealizedPnl > 0).Sum(p => p.RealizedPnl);
        var loss = -closed.Where(p => p.RealizedPnl < 0).Sum(p => p.RealizedPnl);
        var rs = closed.Where(p => p.RMultiple is not null).Select(p => (double)p.RMultiple!.Value).ToList();
        PaperBucket Bucket(string key, List<PaperPosition> ps) => new(key, ps.Count, ps.Count == 0 ? 0 : (double)ps.Count(p => p.RealizedPnl > 0) / ps.Count, ps.Sum(p => p.RealizedPnl),
            ps.Any(p => p.RMultiple is not null) ? ps.Where(p => p.RMultiple is not null).Average(p => (double)p.RMultiple!.Value) : null);
        return new PaperStats(closed.Count, wins, closed.Count == 0 ? 0 : (double)wins / closed.Count, closed.Sum(p => p.RealizedPnl), gross, loss,
            loss > 0 ? (double)(gross / loss) : null, closed.Count == 0 ? 0 : closed.Average(p => p.RealizedPnl),
            rs.Count > 0 ? rs.Average() : null, rs.Count > 0 ? rs.Average() : null,
            closed.GroupBy(p => p.SetupAtEntry ?? "Unknown").Select(g => Bucket(g.Key, g.ToList())).OrderByDescending(b => b.Trades).ToList(),
            closed.GroupBy(p => p.RegimeAtEntry ?? "Unknown").Select(g => Bucket(g.Key, g.ToList())).OrderByDescending(b => b.Trades).ToList());
    }
}

/// <summary>Feeds every open position's symbol with the latest price once per scanner cycle.</summary>
public sealed class PaperPriceFeed : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly PaperEngine _engine;
    private readonly IPaperRepository _repo;
    private readonly ScannerService _scanner;
    private readonly ILogger<PaperPriceFeed> _logger;
    private int _busy;

    public PaperPriceFeed(PaperEngine engine, IPaperRepository repo, ScannerService scanner, ILogger<PaperPriceFeed> logger)
    {
        _engine = engine; _repo = repo; _scanner = scanner; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnSnapshot(ScannerSnapshot s) => _ = TickAllAsync(s, stoppingToken);
        _scanner.SnapshotPublished += OnSnapshot;
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { _scanner.SnapshotPublished -= OnSnapshot; }
    }

    private async Task TickAllAsync(ScannerSnapshot s, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var open = (await _repo.ListPositionsAsync(ct).ConfigureAwait(false)).Where(p => p.Status == PaperPositionStatus.Open).Select(p => p.Symbol).Distinct();
            foreach (var symbol in open)
            {
                var o = s.Get(new Symbol(symbol));
                if (o is null || o.Quality.Stale) continue; // never trigger stops on stale prices
                await _engine.TickAsync(symbol, (decimal)o.Price, s.At, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Paper price feed failed"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
