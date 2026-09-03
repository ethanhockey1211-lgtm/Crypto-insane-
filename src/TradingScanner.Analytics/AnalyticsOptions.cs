namespace TradingScanner.Analytics;

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";
    public int RsiPeriod { get; set; } = 14;
    public int AtrPeriod { get; set; } = 14;
    public int RelativeVolumeBaseline { get; set; } = 20;
    public int RelativeVolumeFast { get; set; } = 3;
    public int RealizedVolatilityPeriod { get; set; } = 20;
    public Structure.StructureOptions Structure { get; set; } = new();
}
