using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using TradingScanner.Signals.Explain;

namespace TradingScanner.Api.Services;

public sealed record AnthropicOptions
{
    public const string SectionName = "Anthropic";
    /// <summary>Read from ANTHROPIC_API_KEY when empty; the key never leaves the server.</summary>
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-opus-5";
    public int MaxTokens { get; set; } = 2048;
}

/// <summary>
/// Thin adapter over the official Anthropic SDK. One short completion per call; adaptive thinking is the model's
/// default and effort is kept low because the input is already fully structured. Server-side refusal fallbacks are
/// enabled so a declined request is rerouted rather than failing the setup card.
/// </summary>
public sealed class AnthropicExplanationModel : IExplanationModel
{
    private readonly AnthropicClient _client;
    private readonly AnthropicOptions _o;
    private readonly ILogger<AnthropicExplanationModel> _logger;

    public AnthropicExplanationModel(AnthropicOptions options, ILogger<AnthropicExplanationModel> logger)
    {
        _o = options;
        _logger = logger;
        _client = string.IsNullOrWhiteSpace(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
    }

    public string ModelName => _o.Model;

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        try
        {
            var response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _o.Model,
                MaxTokens = _o.MaxTokens,
                System = system,
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new BetaFallbacksParam(new Default()),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                Messages = [new BetaMessageParam { Role = Role.User, Content = user }],
            }, cancellationToken: ct);

            if (response.StopReason?.ToString() == "refusal")
                throw new InvalidOperationException("The model declined to explain this setup.");
            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The model returned no text.");
            return text;
        }
        catch (AnthropicRateLimitException ex)
        {
            _logger.LogWarning(ex, "Anthropic rate limited");
            throw new InvalidOperationException("AI explanation is rate limited right now; try again shortly.", ex);
        }
        catch (Anthropic4xxException ex)
        {
            _logger.LogWarning(ex, "Anthropic request rejected");
            throw new InvalidOperationException($"AI explanation request rejected ({ex.GetType().Name}). Check the API key and model.", ex);
        }
        catch (AnthropicApiException ex)
        {
            _logger.LogWarning(ex, "Anthropic request failed");
            throw new InvalidOperationException("AI explanation is temporarily unavailable.", ex);
        }
    }
}
