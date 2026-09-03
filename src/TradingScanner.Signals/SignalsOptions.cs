using TradingScanner.Signals.Breakouts;

namespace TradingScanner.Signals;

public sealed class SignalsOptions
{
    public const string SectionName = "Signals";
    public BreakoutOptions Breakout { get; set; } = new();
    public Scanner.ScannerOptions Scanner { get; set; } = new();
}
