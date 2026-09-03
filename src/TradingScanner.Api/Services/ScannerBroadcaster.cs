using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using TradingScanner.Api.Contracts;
using TradingScanner.Api.Hubs;
using TradingScanner.Signals.Alerts;
using TradingScanner.Signals.Paper;
using TradingScanner.Signals.Scanner;
using TradingScanner.Signals.Tape;

namespace TradingScanner.Api.Services;

/// <summary>Forwards scanner snapshots ("scanner") and tape events ("tape") to SignalR clients off the scanner thread.</summary>
public sealed class ScannerBroadcaster : BackgroundService
{
    private readonly ScannerService _scanner;
    private readonly AlertService _alerts;
    private readonly PaperEngine _paper;
    private readonly IHubContext<MarketHub> _hub;
    private readonly ILogger<ScannerBroadcaster> _logger;
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    public ScannerBroadcaster(ScannerService scanner, AlertService alerts, PaperEngine paper, IHubContext<MarketHub> hub, ILogger<ScannerBroadcaster> logger)
    {
        _scanner = scanner;
        _alerts = alerts;
        _paper = paper;
        _hub = hub;
        _logger = logger;
    }

    public static ScannerStreamDto ToStream(ScannerSnapshot s) => new(s.At, s.Market, s.Opportunities.Select(ScannerRowDto.From).ToList(), s.Universe, s.CycleMs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnSnapshot(ScannerSnapshot s) => _queue.Writer.TryWrite(s);
        void OnTape(TapeEvent e) => _queue.Writer.TryWrite(e);
        void OnAlert(AlertEvent e) => _queue.Writer.TryWrite(e);
        void OnFill(PaperFill f) => _queue.Writer.TryWrite(f);
        _scanner.SnapshotPublished += OnSnapshot;
        _scanner.Tape.Published += OnTape;
        _alerts.Fired += OnAlert;
        _paper.Filled += OnFill;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    switch (item)
                    {
                        case ScannerSnapshot s: await _hub.Clients.All.SendAsync("scanner", ToStream(s), stoppingToken); break;
                        case TapeEvent e: await _hub.Clients.All.SendAsync("tape", e, stoppingToken); break;
                        case AlertEvent a: await _hub.Clients.All.SendAsync("alert", a, stoppingToken); break;
                        case PaperFill f: await _hub.Clients.All.SendAsync("paper", f, stoppingToken); break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Scanner broadcast failed");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _scanner.SnapshotPublished -= OnSnapshot;
            _scanner.Tape.Published -= OnTape;
            _alerts.Fired -= OnAlert;
            _paper.Filled -= OnFill;
        }
    }
}
