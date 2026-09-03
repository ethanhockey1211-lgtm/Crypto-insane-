using System.Net.WebSockets;
using System.Text;
using TradingScanner.Core.Providers;

namespace TradingScanner.Tests.Support;

public abstract record SocketStep;
public sealed record SendStep(string Json) : SocketStep;
public sealed record CloseStep : SocketStep;
public sealed record ThrowStep(Exception Exception) : SocketStep;
/// <summary>Block until the test releases the gate, then continue with the next step.</summary>
public sealed record GateStep(TaskCompletionSource Gate) : SocketStep;

/// <summary>Deterministic IWebSocketClient that plays a script of server messages. Records what the client sent.</summary>
public sealed class ScriptedWebSocket : IWebSocketClient
{
    private readonly Queue<SocketStep> _steps;
    private byte[]? _remainder;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedWebSocket(IEnumerable<SocketStep> steps) => _steps = new Queue<SocketStep>(steps);

    public Exception? ConnectException { get; init; }
    public List<string> Sent { get; } = new();
    public WebSocketState State { get; private set; } = WebSocketState.None;
    public bool Aborted { get; private set; }
    public Task Connected => _connected.Task;

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        if (ConnectException is not null) throw ConnectException;
        State = WebSocketState.Open;
        _connected.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
    {
        Sent.Add(Encoding.UTF8.GetString(utf8.Span));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_remainder is not null) return Deliver(_remainder, buffer);

        while (true)
        {
            if (_steps.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new OperationCanceledException(ct);
            }
            var step = _steps.Dequeue();
            switch (step)
            {
                case SendStep s:
                    return Deliver(Encoding.UTF8.GetBytes(s.Json), buffer);
                case CloseStep:
                    State = WebSocketState.CloseReceived;
                    return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                case ThrowStep t:
                    State = WebSocketState.Aborted;
                    throw t.Exception;
                case GateStep g:
                    await g.Gate.Task.WaitAsync(ct);
                    continue;
            }
        }
    }

    private ValueWebSocketReceiveResult Deliver(byte[] bytes, Memory<byte> buffer)
    {
        if (bytes.Length <= buffer.Length)
        {
            bytes.CopyTo(buffer);
            _remainder = null;
            return new ValueWebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }
        bytes.AsSpan(0, buffer.Length).CopyTo(buffer.Span);
        _remainder = bytes[buffer.Length..];
        return new ValueWebSocketReceiveResult(buffer.Length, WebSocketMessageType.Text, false);
    }

    public Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        State = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public void Abort() { Aborted = true; State = WebSocketState.Aborted; }
    public void Dispose() { }
}

public sealed class ScriptedSocketFactory : IWebSocketClientFactory
{
    private readonly Queue<ScriptedWebSocket> _sockets = new();
    public List<ScriptedWebSocket> Created { get; } = new();

    public ScriptedSocketFactory(params ScriptedWebSocket[] sockets)
    {
        foreach (var s in sockets) _sockets.Enqueue(s);
    }

    public void Enqueue(ScriptedWebSocket socket) => _sockets.Enqueue(socket);

    public IWebSocketClient Create()
    {
        // When the script is exhausted, hand out sockets that connect and then wait silently so the test can cancel cleanly.
        var s = _sockets.Count > 0 ? _sockets.Dequeue() : new ScriptedWebSocket([]);
        Created.Add(s);
        return s;
    }
}
