using TradingScanner.Core.Market;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Core;

public class TimeframeTests
{
    [Theory]
    [InlineData(Timeframe.M1, "2024-03-01T12:34:56.789Z", "2024-03-01T12:34:00Z")]
    [InlineData(Timeframe.M3, "2024-03-01T12:34:56Z", "2024-03-01T12:33:00Z")]
    [InlineData(Timeframe.M5, "2024-03-01T12:34:56Z", "2024-03-01T12:30:00Z")]
    [InlineData(Timeframe.M15, "2024-03-01T12:44:59Z", "2024-03-01T12:30:00Z")]
    [InlineData(Timeframe.M30, "2024-03-01T12:59:59Z", "2024-03-01T12:30:00Z")]
    [InlineData(Timeframe.H1, "2024-03-01T12:59:59Z", "2024-03-01T12:00:00Z")]
    [InlineData(Timeframe.H4, "2024-03-01T13:00:00Z", "2024-03-01T12:00:00Z")]
    [InlineData(Timeframe.H4, "2024-03-01T11:59:59Z", "2024-03-01T08:00:00Z")]
    [InlineData(Timeframe.H4, "2024-03-01T00:00:00Z", "2024-03-01T00:00:00Z")]
    public void BucketStart_aligns_to_epoch_in_utc(Timeframe tf, string input, string expected)
    {
        Assert.Equal(T.At(expected), tf.BucketStart(T.At(input)));
    }

    [Fact]
    public void BucketStart_is_independent_of_input_offset()
    {
        var local = new DateTimeOffset(2024, 3, 1, 7, 34, 56, TimeSpan.FromHours(-5)); // 12:34:56Z
        Assert.Equal(T.At("2024-03-01T12:30:00Z"), Timeframe.M5.BucketStart(local));
    }

    [Fact]
    public void BucketEnd_is_start_plus_duration()
    {
        Assert.Equal(T.At("2024-03-01T12:35:00Z"), Timeframe.M5.BucketEnd(T.At("2024-03-01T12:34:56Z")));
    }

    [Theory]
    [InlineData("1m", Timeframe.M1)]
    [InlineData("15M", Timeframe.M15)]
    [InlineData(" 4h ", Timeframe.H4)]
    [InlineData("h1", Timeframe.H1)]
    public void TryParse_accepts_labels(string text, Timeframe expected)
    {
        Assert.True(TimeframeExtensions.TryParse(text, out var tf));
        Assert.Equal(expected, tf);
    }

    [Theory]
    [InlineData("2m")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_rejects_unknown(string? text)
    {
        Assert.False(TimeframeExtensions.TryParse(text, out _));
    }

    [Fact]
    public void Symbol_normalizes_and_validates()
    {
        var s = new Symbol("btc-usd");
        Assert.Equal("BTC-USD", s.Value);
        Assert.Equal("BTC", s.Base);
        Assert.Equal("USD", s.Quote);
        Assert.Throws<ArgumentException>(() => new Symbol("BTCUSD"));
        Assert.Throws<ArgumentException>(() => new Symbol("-USD"));
    }
}
