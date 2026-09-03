using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradingScanner.Core;
using TradingScanner.Core.Market;

namespace TradingScanner.MarketData.Engine;

/// <summary>Single bounded channel between all providers (many writers) and the engine (one reader).</summary>
public sealed class MarketEventChannel
{
    private readonly Channel<MarketEvent> _channel;

    public MarketEventChannel(IOptions<MarketDataOptions> options) : this(options.Value.IngestChannelCapacity) { }

    public MarketEventChannel(int capacity)
    {
        _channel = Channel.CreateBounded<MarketEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<MarketEvent> Reader => _channel.Reader;
    public ChannelWriter<MarketEvent> Writer => _channel.Writer;
    public int Depth => _channel.Reader.Count;
}
