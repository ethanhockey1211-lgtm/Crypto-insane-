using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Signals.Alerts;

/// <summary>
/// Evaluates every enabled rule against every scanner cycle. Rules without a symbol are evaluated per symbol.
/// Firing appends an event, raises <see cref="Fired"/> (for the hub), and posts to a webhook when configured.
/// </summary>
public sealed class AlertService : BackgroundService
{
    private readonly ScannerService _scanner;
    private readonly IAlertRepository _repo;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AlertService> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(Guid rule, string symbol), AlertState> _states = new();
    private IReadOnlyList<AlertRule> _rules = [];
    private int _evaluating;

    public AlertService(ScannerService scanner, IAlertRepository repo, IHttpClientFactory http, ILogger<AlertService> logger, TimeProvider? time = null)
    {
        _scanner = scanner;
        _repo = repo;
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public event Action<AlertEvent>? Fired;

    /// <summary>Reload the rule cache after a change through the API.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        _rules = await _repo.ListRulesAsync(ct).ConfigureAwait(false);
        foreach (var key in _states.Keys.Where(k => _rules.All(r => r.Id != k.rule)).ToList()) _states.TryRemove(key, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReloadAsync(stoppingToken).ConfigureAwait(false);
        void OnSnapshot(ScannerSnapshot s) => _ = EvaluateAsync(s, stoppingToken);
        _scanner.SnapshotPublished += OnSnapshot;
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { _scanner.SnapshotPublished -= OnSnapshot; }
    }

    /// <summary>Evaluate one snapshot. Public so tests can drive it deterministically.</summary>
    public async Task<List<AlertEvent>> EvaluateAsync(ScannerSnapshot snapshot, CancellationToken ct)
    {
        var fired = new List<AlertEvent>();
        if (Interlocked.Exchange(ref _evaluating, 1) == 1) return fired; // a slow webhook must not pile up cycles
        try
        {
            var now = snapshot.At;
            foreach (var rule in _rules)
            {
                if (!rule.Enabled) continue;
                IEnumerable<Opportunity> targets = rule.Symbol is { } s ? snapshot.Opportunities.Where(o => o.Symbol.Value.Equals(s, StringComparison.OrdinalIgnoreCase)) : snapshot.Opportunities;
                foreach (var o in targets)
                {
                    var values = AlertValues.From(o, snapshot.Market);
                    var state = _states.GetOrAdd((rule.Id, o.Symbol.Value), _ => new AlertState());
                    var decision = AlertEvaluator.Evaluate(rule, values, state, now);
                    if (!decision.Fire) continue;
                    var evt = new AlertEvent(Guid.NewGuid(), rule.Id, rule.Name, now, o.Symbol.Value, BuildMessage(rule, o, values, decision),
                        rule.Conditions.Select(c => c.Field).Distinct().ToDictionary(f => f.ToString(), f => values[f].ToString()));
                    fired.Add(evt);
                    await _repo.AppendEventAsync(evt, ct).ConfigureAwait(false);
                    await _repo.UpsertRuleAsync(rule with { LastFiredAt = now }, ct).ConfigureAwait(false);
                    _logger.LogInformation("Alert fired: {Rule} on {Symbol}: {Message}", rule.Name, o.Symbol, evt.Message);
                    Fired?.Invoke(evt);
                    if (rule.Channels.Contains("webhook") && rule.WebhookUrl is { } url) await PostWebhookAsync(url, evt, ct).ConfigureAwait(false);
                }
            }
            if (fired.Count > 0) _rules = await _repo.ListRulesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Alert evaluation failed");
        }
        finally
        {
            Interlocked.Exchange(ref _evaluating, 0);
        }
        return fired;
    }

    private static string BuildMessage(AlertRule rule, Opportunity o, IReadOnlyDictionary<AlertField, AlertValue> values, AlertDecision d)
    {
        var conds = string.Join(" AND ", rule.Conditions.Select(c => $"{c.Field} {AlertEvaluator.Op(c.Operator)} {c.Value} (now {values[c.Field]})"));
        var held = d.HeldFor is { } h && rule.HoldSeconds > 0 ? $" held {h.TotalSeconds:F0}s" : "";
        return $"{o.Symbol.Value} @ {o.Price:G6}: {conds}{held}. Score {o.Score:F0}, setup {o.Setup.Type}.";
    }

    private async Task PostWebhookAsync(string url, AlertEvent evt, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient("alerts");
            client.Timeout = TimeSpan.FromSeconds(5);
            // Discord-compatible body ("content") plus the full event for generic receivers (Telegram bridges, email relays).
            using var resp = await client.PostAsJsonAsync(url, new { content = $"[{evt.RuleName}] {evt.Message}", @event = evt }, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) _logger.LogWarning("Webhook for {Rule} returned {Status}", evt.RuleName, (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Webhook for {Rule} failed", evt.RuleName);
        }
    }
}
