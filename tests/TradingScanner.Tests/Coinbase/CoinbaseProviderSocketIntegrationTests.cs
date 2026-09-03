using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingScanner.Core.Market;
using TradingScanner.MarketData.Coinbase;
using TradingScanner.MarketData.Metrics;
using TradingScanner.MarketData.WebSockets;
using TradingScanner.Tests.Fixtures;
using TradingScanner.Tests.Support;
using Xunit;

namespace TradingScanner.Tests.Coinbase;

/// <summary>
/// Runs the real ClientWebSocket adapter against an in-process Kestrel WebSocket server that speaks the
/// Coinbase protocol and drops the first connection. Proves the adapter + reconnect loop end to end.
/// </summary>
public class CoinbaseProviderSocketIntegrationTests
{
    [Fact]
    public async Task Real_socket_connects_receives_trades_survives_a_server_drop_and_resubscribes()
    {
        var connections = 0;
        var subscribeMessages = new List<string>();
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var n = Interlocked.Increment(ref connections);

            var buf = new byte[16 * 1024];
            var res = await ws.ReceiveAsync(buf, ctx.RequestAborted);
            lock (subscribeMessages) subscribeMessages.Add(Encoding.UTF8.GetString(buf, 0, res.Count));

            await Send(ws, CoinbaseFixtures.SubscriptionsFor("BTC-USD"));
            await Send(ws, CoinbaseFixtures.MatchAt("BTC-USD", 1000 + n * 10, $"2024-03-01T12:00:0{n}Z", "50000", "0.01"));
            if (n == 1)
            {
                await Task.Delay(200); // let the client read the frame before the connection is reset
                ws.Abort(); // simulate an unclean drop
                return;
            }
            await Send(ws, CoinbaseFixtures.MatchAt("BTC-USD", 1000 + n * 10 + 1, "2024-03-01T12:00:05Z", "50001", "0.02"));
            await Task.WhenAny(shutdown.Task, Task.Delay(TimeSpan.FromSeconds(30), ctx.RequestAborted).ContinueWith(_ => { }));
            ws.Abort();
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUrl = address.Replace("http://", "ws://") + "/";

        try
        {
            var options = T.FastOptions();
            var metrics = new MarketDataMetrics();
            var provider = new CoinbaseExchangeProvider(new CoinbaseOptions { WebSocketUrl = wsUrl }, options, T.RestClient(), new ClientWebSocketFactory(), metrics, NullLogger<CoinbaseExchangeProvider>.Instance, random: new Random(3));
            var channel = Channel.CreateUnbounded<MarketEvent>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var run = provider.RunAsync([new Symbol("BTC-USD")], channel.Writer, cts.Token);
            var events = await T.CollectAsync(channel.Reader, e => e.Any(x => x.Kind == MarketEventKind.Trade && x.Trade.TradeId == 1021), TimeSpan.FromSeconds(10));

            Assert.Equal(2, connections);
            Assert.Equal(2, subscribeMessages.Count);
            Assert.All(subscribeMessages, m => Assert.Contains("\"subscribe\"", m));
            Assert.Equal([1010L, 1020L, 1021L], events.Where(e => e.Kind == MarketEventKind.Trade).Select(e => e.Trade.TradeId));
            Assert.Equal(1, metrics.Snapshot().WsReconnects);
            Assert.Contains(events, e => e.Kind == MarketEventKind.Gap); // 1011..1019 were never sent: reported, not hidden
            Assert.Equal(FeedStatus.Degraded, provider.Status);

            cts.Cancel();
            await run;
        }
        finally
        {
            shutdown.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stop.Token);
        }
    }

    private static Task Send(WebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}
