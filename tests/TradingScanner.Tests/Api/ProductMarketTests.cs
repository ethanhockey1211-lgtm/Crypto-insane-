using TradingScanner.Api.Product;
using TradingScanner.Signals.Scanner;
using TradingScanner.Tests.Support;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Api;

public class ProductMarketTests
{
    private sealed class PreviewFactory : ApiIntegrationTests.Factory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Product:CommercialMode", "true");
            builder.UseSetting("Product:MarketDataApproved", "false");
        }
    }

    [Fact]
    public async Task Unapproved_preview_does_not_start_the_exchange_connection()
    {
        using var factory = new PreviewFactory();
        using var client = factory.CreateClient();
        var response = await client.GetStringAsync("/api/product/overview");
        using var json = System.Text.Json.JsonDocument.Parse(response);
        Assert.Equal("demo", json.RootElement.GetProperty("mode").GetString());
        Assert.Empty(json.RootElement.GetProperty("rows").EnumerateArray());
        Assert.False(factory.Provider.Started.Task.IsCompleted);
    }

    private static Opportunity Example() => new(Xrp, T.Base, 100, 1, 90, new([], [], 90, 90, 1),
        new(SetupType.TrendPullback, Confidence.High, TrendBias.Bullish, ["Observed evidence"], null, null),
        new(99, 101, "Confirm", 98, 98, 106, 108, 110, 2, 3, 4, 5, [], 101.2, EntryState.InZone),
        new(0, false, [], null, null, null, null, null, null, null), ["Observed evidence"], "Below 98", [],
        new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 4, 10_000_000, null, null, null, null, null),
        null, new(false, 0, true, "test", "test"));

    [Fact]
    public void Customer_fee_projection_keeps_evidence_and_thresholds_unchanged()
    {
        var original = Example();
        var options = new ScannerOptions();
        var cheap = ProductMarketEndpoints.AssessForCustomer(original, Market(), 0, 5, options, T.Base);
        var expensive = ProductMarketEndpoints.AssessForCustomer(original, Market(), 150, 20, options, T.Base);
        Assert.Equal("Watch", cheap.Execution!.Status);
        Assert.Equal("Blocked", expensive.Execution!.Status);
        Assert.True(expensive.Execution.NetRewardRatio < cheap.Execution.NetRewardRatio);
        Assert.Equal(original.Breakdown, expensive.Breakdown);
        Assert.Equal(original.Plan, expensive.Plan);
        Assert.Equal(original.Score, expensive.Score);
        Assert.Null(original.Execution);
        Assert.Equal(60, options.Execution.MinScore);
        Assert.Equal(60, options.Execution.FeeBps);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(-3)]
    public void Frozen_or_future_snapshots_cannot_remain_fresh(int seconds)
    {
        var result = ProductMarketEndpoints.AssessForCustomer(Example(), Market(), 0, 5, new(), T.Base.AddSeconds(seconds));
        Assert.True(result.Quality.Stale);
        Assert.Equal("Blocked", result.Execution!.Status);
    }

    [Fact]
    public void Stale_benchmark_blocks_even_a_fresh_asset()
    {
        var result = ProductMarketEndpoints.AssessForCustomer(Example(), Market() with { At = T.Base.AddMinutes(-1) }, 0, 5, new(), T.Base);
        Assert.Equal("Blocked", result.Execution!.Status);
    }

    [Fact]
    public void Cheaper_fees_do_not_relax_engine_score_or_freshness_gates()
    {
        var result = ProductMarketEndpoints.AssessForCustomer(Example() with { Score = 59.99 }, Market(), 0, 0, new(), T.Base);
        Assert.Equal("Blocked", result.Execution!.Status);
        Assert.Contains(result.Execution.Reasons, x => x.Contains("score"));
    }
}
