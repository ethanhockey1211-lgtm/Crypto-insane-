using System.Collections.Concurrent;

namespace TradingScanner.Signals.Alerts;

public interface IAlertRepository
{
    Task<IReadOnlyList<AlertRule>> ListRulesAsync(CancellationToken ct);
    Task<AlertRule?> GetRuleAsync(Guid id, CancellationToken ct);
    Task UpsertRuleAsync(AlertRule rule, CancellationToken ct);
    Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct);
    Task AppendEventAsync(AlertEvent evt, CancellationToken ct);
    Task<IReadOnlyList<AlertEvent>> ListEventsAsync(int limit, CancellationToken ct);
}

/// <summary>Default store when no database is configured. State is lost on restart, which the UI states.</summary>
public sealed class InMemoryAlertRepository : IAlertRepository
{
    private readonly ConcurrentDictionary<Guid, AlertRule> _rules = new();
    private readonly List<AlertEvent> _events = new();
    private readonly object _gate = new();

    public Task<IReadOnlyList<AlertRule>> ListRulesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AlertRule>>(_rules.Values.OrderBy(r => r.CreatedAt).ToList());
    public Task<AlertRule?> GetRuleAsync(Guid id, CancellationToken ct) => Task.FromResult(_rules.GetValueOrDefault(id));
    public Task UpsertRuleAsync(AlertRule rule, CancellationToken ct) { _rules[rule.Id] = rule; return Task.CompletedTask; }
    public Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct) => Task.FromResult(_rules.TryRemove(id, out _));

    public Task AppendEventAsync(AlertEvent evt, CancellationToken ct)
    {
        lock (_gate) { _events.Add(evt); if (_events.Count > 5000) _events.RemoveRange(0, _events.Count - 5000); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AlertEvent>> ListEventsAsync(int limit, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<AlertEvent>>(_events.AsEnumerable().Reverse().Take(limit).ToList());
    }
}
