using System.Diagnostics;
using TradingScanner.MarketData.Universe;
using Xunit;

namespace TradingScanner.Tests.MarketData;

public class RequestRateLimiterTests
{
    [Fact]
    public async Task Allows_at_most_n_acquisitions_per_window()
    {
        var limiter = new RequestRateLimiter(3, TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) await limiter.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 150, $"first window took {sw.ElapsedMilliseconds}ms");
        await limiter.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 180, $"fourth acquisition waited only {sw.ElapsedMilliseconds}ms");
    }
}
