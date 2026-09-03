using System.Collections.Concurrent;

namespace TradingScanner.Signals.Paper;

public interface IPaperRepository
{
    Task<PaperAccount?> GetAccountAsync(CancellationToken ct);
    Task SaveAccountAsync(PaperAccount account, CancellationToken ct);
    Task SaveOrderAsync(PaperOrder order, CancellationToken ct);
    Task SavePositionAsync(PaperPosition position, CancellationToken ct);
    Task<IReadOnlyList<PaperOrder>> ListOrdersAsync(CancellationToken ct);
    Task<IReadOnlyList<PaperPosition>> ListPositionsAsync(CancellationToken ct);
}

public sealed class InMemoryPaperRepository : IPaperRepository
{
    private PaperAccount? _account;
    private readonly ConcurrentDictionary<Guid, PaperOrder> _orders = new();
    private readonly ConcurrentDictionary<Guid, PaperPosition> _positions = new();

    public Task<PaperAccount?> GetAccountAsync(CancellationToken ct) => Task.FromResult(_account);
    public Task SaveAccountAsync(PaperAccount account, CancellationToken ct) { _account = account; return Task.CompletedTask; }
    public Task SaveOrderAsync(PaperOrder order, CancellationToken ct) { _orders[order.Id] = order; return Task.CompletedTask; }
    public Task SavePositionAsync(PaperPosition position, CancellationToken ct) { _positions[position.Id] = position; return Task.CompletedTask; }
    public Task<IReadOnlyList<PaperOrder>> ListOrdersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PaperOrder>>(_orders.Values.OrderByDescending(o => o.CreatedAt).ToList());
    public Task<IReadOnlyList<PaperPosition>> ListPositionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PaperPosition>>(_positions.Values.OrderByDescending(p => p.OpenedAt).ToList());
}
