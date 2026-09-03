using System.Collections.Concurrent;

namespace TradingScanner.Signals.Performance;

public interface ISignalRepository
{
    Task SaveAsync(SignalRecord signal, SignalOutcome outcome, CancellationToken ct);
    Task<IReadOnlyList<SignalWithOutcome>> ListAsync(int limit, string? symbol, CancellationToken ct);
    Task<IReadOnlyList<SignalWithOutcome>> ListIncompleteAsync(CancellationToken ct);
}

public sealed class InMemorySignalRepository : ISignalRepository
{
    private readonly ConcurrentDictionary<Guid, SignalWithOutcome> _items = new();
    private readonly int _max;

    public InMemorySignalRepository(int max = 20_000) => _max = max;

    public Task SaveAsync(SignalRecord signal, SignalOutcome outcome, CancellationToken ct)
    {
        _items[signal.Id] = new SignalWithOutcome(signal, outcome);
        if (_items.Count > _max)
            foreach (var old in _items.Values.OrderBy(x => x.Signal.At).Take(_items.Count - _max)) _items.TryRemove(old.Signal.Id, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SignalWithOutcome>> ListAsync(int limit, string? symbol, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SignalWithOutcome>>(_items.Values.Where(x => symbol is null || x.Signal.Symbol == symbol).OrderByDescending(x => x.Signal.At).Take(limit).ToList());

    public Task<IReadOnlyList<SignalWithOutcome>> ListIncompleteAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SignalWithOutcome>>(_items.Values.Where(x => !x.Outcome.Complete).ToList());
}
