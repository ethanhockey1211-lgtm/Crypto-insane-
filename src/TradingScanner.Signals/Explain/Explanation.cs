using System.Text.Json;
using System.Text.Json.Serialization;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Signals.Explain;

public sealed record Explanation(
    string Summary,
    IReadOnlyList<string> Why,
    IReadOnlyList<string> Invalidation,
    IReadOnlyList<string> Risks,
    bool? AppearsExtended,
    string Model,
    DateTimeOffset At,
    string Disclaimer);

/// <summary>A text model that completes one prompt. The only thing the Anthropic adapter implements.</summary>
public interface IExplanationModel
{
    string ModelName { get; }
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}

public interface IExplanationService
{
    bool Enabled { get; }
    Task<Explanation> ExplainAsync(Opportunity opportunity, MarketContext market, CancellationToken ct);
}

/// <summary>
/// Turns the deterministic engine's structured output into a narrative. The model receives only numbers the engine
/// computed and is told to use nothing else; it never calculates and never sees raw market data. Responses are
/// cached briefly per symbol so a busy setup card does not re-bill every refresh.
/// </summary>
public sealed class ExplanationService : IExplanationService
{
    public const string DisclaimerText = "Narrative generated from the engine's numbers. Not a prediction, not advice; no setup is guaranteed.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false, Converters = { new JsonStringEnumConverter() } };
    private readonly IExplanationModel? _model;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (DateTimeOffset at, Explanation value)> _cache = new();
    private readonly object _gate = new();

    public ExplanationService(IExplanationModel? model, TimeProvider? time = null)
    {
        _model = model;
        _time = time ?? TimeProvider.System;
    }

    public bool Enabled => _model is not null;
    public TimeSpan CacheFor { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<Explanation> ExplainAsync(Opportunity o, MarketContext market, CancellationToken ct)
    {
        if (_model is null) throw new InvalidOperationException("AI explanation is not configured. Set ANTHROPIC_API_KEY to enable it.");
        var key = $"{o.Symbol.Value}|{o.At:O}|{o.Setup.Type}|{Math.Round(o.Score)}";
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit) && _time.GetUtcNow() - hit.at < CacheFor) return hit.value;
        }
        var text = await _model.CompleteAsync(SystemPrompt, BuildUserPrompt(o, market), ct).ConfigureAwait(false);
        var explanation = Parse(text, _model.ModelName, _time.GetUtcNow());
        lock (_gate)
        {
            _cache[key] = (_time.GetUtcNow(), explanation);
            if (_cache.Count > 500) foreach (var stale in _cache.Where(kv => _time.GetUtcNow() - kv.Value.at > CacheFor).Select(kv => kv.Key).ToList()) _cache.Remove(stale);
        }
        return explanation;
    }

    public const string SystemPrompt =
        "You explain the output of a deterministic crypto scanning engine to a discretionary day trader. " +
        "You receive a JSON object with every number the engine computed: setup classification, score components and penalties with their evidence, " +
        "the trade plan, overextension flags, momentum, volume, structure levels and the BTC/market context. " +
        "Rules: use only the numbers and facts in the JSON; never invent, estimate, or infer values that are not present; if something is missing say it is unavailable. " +
        "Never call any outcome certain, guaranteed or likely; describe evidence and conditions. Prices must be quoted exactly as given. " +
        "Be concise and concrete, in the voice of an experienced desk analyst. " +
        "Respond with a single JSON object and nothing else, with keys: summary (2-3 sentences on why the setup is where it is), " +
        "why (array of short strings, the strongest evidence), invalidation (array of short strings: what would invalidate the thesis and where), " +
        "risks (array of short strings), appearsExtended (boolean: whether the move already looks extended based on the overextension data).";

    public static string BuildUserPrompt(Opportunity o, MarketContext market)
    {
        var payload = new
        {
            symbol = o.Symbol.Value,
            asOf = o.At,
            price = o.Price,
            rank = o.Rank,
            score = o.Score,
            setup = new { type = o.Setup.Type.ToString(), confidence = o.Setup.Confidence.ToString(), bias = o.Setup.Bias.ToString(), evidence = o.Setup.Evidence, keyLevel = o.Setup.KeyLevel, breakout = o.Setup.Breakout is { } b ? new { state = b.State.ToString(), level = b.Level.Price, touches = b.Level.Touches, barsSinceBreakout = b.BarsSinceBreakout, breakoutRelVol = b.BreakoutRelVol, weakVolume = b.WeakVolume, distanceAtr = b.DistanceAtr, narrative = b.Narrative } : null },
            scoreComponents = o.Breakdown.Components.Select(c => new { c.Name, c.Points, c.Max, c.Evidence }),
            penalties = o.Breakdown.Penalties.Select(c => new { c.Name, c.Points, c.Max, c.Evidence }),
            plan = o.Plan is { } p ? new { p.EntryLow, p.EntryHigh, p.Trigger, p.Invalidation, p.Stop, p.Target1, p.Target2, p.Target3, p.RewardRatio1, p.RewardRatio2, p.RewardRatio3, p.Basis } : null,
            overextension = new { o.Overextension.Score, o.Overextension.DoNotChase, o.Overextension.Flags, o.Overextension.VwapSigma, o.Overextension.Ema20DistanceAtr, o.Overextension.Move5mAtr, o.Overextension.Move15mAtr, o.Overextension.Move1hPct, o.Overextension.Move24hPct, o.Overextension.SupportDistanceAtr },
            metrics = o.Metrics,
            engineWhy = o.Why,
            engineInvalidation = o.Invalidation,
            engineRisks = o.Risks,
            market = new { regime = market.Regime.ToString(), market.AltsFavorable, btc = market.Btc, market.BreadthAboveVwap, market.BreadthPositive1h, market.MedianRelVolume, market.Notes },
            dataQuality = o.Quality,
        };
        return JsonSerializer.Serialize(payload, Json);
    }

    public static Explanation Parse(string text, string model, DateTimeOffset at)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var root = doc.RootElement;
                return new Explanation(
                    Str(root, "summary") ?? text.Trim(),
                    Arr(root, "why"), Arr(root, "invalidation"), Arr(root, "risks"),
                    root.TryGetProperty("appearsExtended", out var ext) && ext.ValueKind is JsonValueKind.True or JsonValueKind.False ? ext.GetBoolean() : null,
                    model, at, DisclaimerText);
            }
            catch (JsonException) { /* fall through: keep the raw text */ }
        }
        return new Explanation(text.Trim(), [], [], [], null, model, at, DisclaimerText);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static List<string> Arr(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray()) if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && s.Length > 0) list.Add(s);
        return list;
    }
}
