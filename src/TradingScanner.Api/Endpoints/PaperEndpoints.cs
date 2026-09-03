using Microsoft.AspNetCore.Http.HttpResults;
using TradingScanner.Signals.Paper;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Api.Endpoints;

public sealed record PaperPositionView(PaperPosition Position, double? LastPrice, decimal? UnrealizedPnl, decimal? MarketValue);
public sealed record PaperAccountView(PaperAccount Account, decimal Equity, decimal OpenValue, decimal UnrealizedPnl, decimal RealizedPnl, int OpenPositions);

public static class PaperEndpoints
{
    public static IEndpointRouteBuilder MapPaperEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/paper").RequireRateLimiting("api");

        g.MapGet("/account", async (PaperEngine engine, IPaperRepository repo, IScannerReader scanner, CancellationToken ct) =>
        {
            var account = await engine.GetOrCreateAccountAsync(ct);
            var positions = await repo.ListPositionsAsync(ct);
            var views = positions.Where(p => p.Status == PaperPositionStatus.Open).Select(p => View(p, scanner)).ToList();
            var openValue = views.Sum(v => v.MarketValue ?? v.Position.AvgEntry * v.Position.Quantity);
            var unrealized = views.Sum(v => v.UnrealizedPnl ?? 0);
            var realized = positions.Sum(p => p.RealizedPnl);
            return Results.Ok(new PaperAccountView(account, account.Cash + openValue, openValue, unrealized, realized, views.Count));
        });

        g.MapPost("/account/reset", async (ResetRequest? req, PaperEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.ResetAccountAsync(req?.StartingBalance is > 0 ? req.StartingBalance.Value : 10_000m, ct)));

        g.MapPost("/orders", async Task<Results<Ok<PaperOrder>, BadRequest<string>>> (PlaceOrderRequest req, PaperEngine engine, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Symbol)) return TypedResults.BadRequest("Symbol is required.");
            if (req.Quantity is null && req.Notional is null) return TypedResults.BadRequest("Quantity or notional is required.");
            var order = await engine.PlaceAsync(req, ct);
            return order.Status == PaperOrderStatus.Rejected ? TypedResults.BadRequest(order.Note ?? "rejected") : TypedResults.Ok(order);
        });

        g.MapGet("/orders", async (IPaperRepository repo, CancellationToken ct) => Results.Ok(await repo.ListOrdersAsync(ct)));
        g.MapDelete("/orders/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, PaperEngine engine, CancellationToken ct) =>
            await engine.CancelAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        g.MapGet("/positions", async (IPaperRepository repo, IScannerReader scanner, CancellationToken ct) =>
            Results.Ok((await repo.ListPositionsAsync(ct)).Where(p => p.Status == PaperPositionStatus.Open).Select(p => View(p, scanner)).ToList()));

        g.MapGet("/trades", async (IPaperRepository repo, CancellationToken ct) =>
            Results.Ok((await repo.ListPositionsAsync(ct)).Where(p => p.Status == PaperPositionStatus.Closed).OrderByDescending(p => p.ClosedAt).ToList()));

        g.MapGet("/stats", async (IPaperRepository repo, CancellationToken ct) => Results.Ok(PaperEngine.ComputeStats(await repo.ListPositionsAsync(ct))));
        return app;
    }

    private static PaperPositionView View(PaperPosition p, IScannerReader scanner)
    {
        var o = scanner.Get(new TradingScanner.Core.Market.Symbol(p.Symbol));
        if (o is null) return new PaperPositionView(p, null, null, null);
        var price = (decimal)o.Price;
        return new PaperPositionView(p, o.Price, (price - p.AvgEntry) * p.Quantity, price * p.Quantity);
    }

    public sealed record ResetRequest(decimal? StartingBalance);
}
