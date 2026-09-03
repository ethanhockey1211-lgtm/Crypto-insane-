using System.Net.WebSockets;

namespace TradingScanner.Core.Providers;

/// <summary>Thin seam over ClientWebSocket so providers can be tested against scripted or in-process sockets.</summary>
public interface IWebSocketClient : IDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, CancellationToken ct);
    ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct);
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
    Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct);
    void Abort();
}

public interface IWebSocketClientFactory
{
    IWebSocketClient Create();
}
