using System.Net.WebSockets;
using TradingScanner.Core.Providers;

namespace TradingScanner.MarketData.WebSockets;

public sealed class ClientWebSocketAdapter : IWebSocketClient
{
    private readonly ClientWebSocket _ws = new();

    public ClientWebSocketAdapter()
    {
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public WebSocketState State => _ws.State;

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);

    public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct) =>
        _ws.SendAsync(utf8, WebSocketMessageType.Text, endOfMessage: true, ct);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) =>
        _ws.ReceiveAsync(buffer, ct);

    public Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct) =>
        _ws.CloseOutputAsync(status, description, ct);

    public void Abort() => _ws.Abort();

    public void Dispose() => _ws.Dispose();
}

public sealed class ClientWebSocketFactory : IWebSocketClientFactory
{
    public IWebSocketClient Create() => new ClientWebSocketAdapter();
}
