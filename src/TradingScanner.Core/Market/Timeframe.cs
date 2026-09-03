namespace TradingScanner.Core.Market;

/// <summary>Candle timeframe. The enum value is the bar length in seconds.</summary>
public enum Timeframe
{
    M1 = 60,
    M3 = 180,
    M5 = 300,
    M15 = 900,
    M30 = 1800,
    H1 = 3600,
    H4 = 14400,
}

public static class TimeframeExtensions
{
    public static readonly Timeframe[] All =
    [
        Timeframe.M1, Timeframe.M3, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4,
    ];

    public static int Seconds(this Timeframe tf) => (int)tf;
    public static TimeSpan Duration(this Timeframe tf) => TimeSpan.FromSeconds((int)tf);

    /// <summary>
    /// Start of the bucket containing <paramref name="t"/>, aligned to the Unix epoch in UTC.
    /// This matches exchange convention (a 4h bar starts at 00:00, 04:00, ... UTC).
    /// </summary>
    public static DateTimeOffset BucketStart(this Timeframe tf, DateTimeOffset t)
    {
        var unix = t.ToUnixTimeSeconds();
        var size = (long)tf;
        var start = unix - ((unix % size) + size) % size; // floor for negative values too
        return DateTimeOffset.FromUnixTimeSeconds(start);
    }

    public static DateTimeOffset BucketEnd(this Timeframe tf, DateTimeOffset t) => tf.BucketStart(t) + tf.Duration();

    public static bool TryParse(string? text, out Timeframe tf)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "1m": case "m1": tf = Timeframe.M1; return true;
            case "3m": case "m3": tf = Timeframe.M3; return true;
            case "5m": case "m5": tf = Timeframe.M5; return true;
            case "15m": case "m15": tf = Timeframe.M15; return true;
            case "30m": case "m30": tf = Timeframe.M30; return true;
            case "1h": case "h1": tf = Timeframe.H1; return true;
            case "4h": case "h4": tf = Timeframe.H4; return true;
            default: tf = default; return false;
        }
    }

    public static string Label(this Timeframe tf) => tf switch
    {
        Timeframe.M1 => "1m",
        Timeframe.M3 => "3m",
        Timeframe.M5 => "5m",
        Timeframe.M15 => "15m",
        Timeframe.M30 => "30m",
        Timeframe.H1 => "1h",
        Timeframe.H4 => "4h",
        _ => ((int)tf).ToString() + "s",
    };
}
