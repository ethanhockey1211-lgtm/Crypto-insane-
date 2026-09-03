namespace TradingScanner.Core.Market;

/// <summary>
/// Canonical symbol identifier, e.g. "BTC-USD". Base and quote are separated by '-'.
/// Provider-specific names (Kraken "XBT/USD") are mapped inside the provider adapter.
/// </summary>
public readonly record struct Symbol
{
    public string Value { get; }

    public Symbol(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Symbol cannot be empty.", nameof(value));
        var dash = value.IndexOf('-');
        if (dash <= 0 || dash == value.Length - 1) throw new ArgumentException($"Symbol '{value}' must be BASE-QUOTE.", nameof(value));
        Value = value.ToUpperInvariant();
    }

    public string Base => Value[..Value.IndexOf('-')];
    public string Quote => Value[(Value.IndexOf('-') + 1)..];

    public override string ToString() => Value;
    public static implicit operator string(Symbol s) => s.Value;
}
