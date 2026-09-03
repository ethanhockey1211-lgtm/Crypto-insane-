using TradingScanner.Core.Time;
using Xunit;

namespace TradingScanner.Tests.Core;

public class BackoffTests
{
    [Fact]
    public void Delays_grow_exponentially_and_cap_with_jitter_inside_bounds()
    {
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), new Random(42));
        var caps = new[] { 1, 2, 4, 8, 16, 30, 30, 30 };
        foreach (var cap in caps)
        {
            var d = b.Next();
            Assert.InRange(d.TotalSeconds, 0.5, cap + 1e-9);
        }
        Assert.Equal(8, b.Attempt);
    }

    [Fact]
    public void Reset_restarts_the_ladder()
    {
        var b = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), new Random(7));
        for (var i = 0; i < 6; i++) b.Next();
        b.Reset();
        Assert.Equal(0, b.Attempt);
        Assert.InRange(b.Next().TotalSeconds, 0.5, 1.0);
    }
}
