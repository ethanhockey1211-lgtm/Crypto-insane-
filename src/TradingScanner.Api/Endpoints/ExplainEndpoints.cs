using Microsoft.AspNetCore.Http.HttpResults;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Explain;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Api.Endpoints;

public static class ExplainEndpoints
{
    public static IEndpointRouteBuilder MapExplainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/scanner/{symbol}/explain", async Task<Results<Ok<Explanation>, NotFound, ProblemHttpResult>> (string symbol, IScannerReader scanner, IExplanationService explain, CancellationToken ct) =>
        {
            if (!explain.Enabled) return TypedResults.Problem("AI explanation is not configured. Set ANTHROPIC_API_KEY on the API server to enable it.", statusCode: StatusCodes.Status503ServiceUnavailable);
            Symbol sym;
            try { sym = new Symbol(symbol); } catch (ArgumentException) { return TypedResults.NotFound(); }
            var snapshot = scanner.Latest;
            var o = snapshot?.Get(sym);
            if (snapshot is null || o is null) return TypedResults.NotFound();
            try { return TypedResults.Ok(await explain.ExplainAsync(o, snapshot.Market, ct)); }
            catch (InvalidOperationException ex) { return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
        }).RequireRateLimiting("api");
        return app;
    }
}
