using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Signals.Performance;

/// <summary>
/// Records every qualifying signal whether or not anyone trades it, then follows its price path for the horizon.
/// Outcomes are pure functions of the recorded plan and the subsequent prices, so a backtest can produce
/// identical records by feeding <see cref="Observe"/> with replayed snapshots.
/// </summary>
public sealed class SignalTracker
{
    private readonly ISignalRepository _repo;
    private readonly PerformanceOptions _o;
    private readonly ILogger<SignalTracker> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRecorded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SignalWithOutcome> _active = new();
    private bool _loaded;

    public SignalTracker(ISignalRepository repo, IOptions<PerformanceOptions> options, ILogger<SignalTracker> logger)
    {
        _repo = repo;
        _o = options.Value;
        _logger = logger;
    }

    public int ActiveCount => _active.Count;

    public async Task LoadAsync(CancellationToken ct)
    {
        foreach (var item in await _repo.ListIncompleteAsync(ct).ConfigureAwait(false)) _active[item.Signal.Id] = item;
        _loaded = true;
    }

    /// <summary>Record new signals from this snapshot and advance every active outcome with its prices.</summary>
    public async Task<List<SignalRecord>> ObserveAsync(ScannerSnapshot snapshot, CancellationToken ct)
    {
        if (!_loaded) await LoadAsync(ct).ConfigureAwait(false);
        var recorded = new List<SignalRecord>();
        var now = snapshot.At;

        foreach (var o in snapshot.Opportunities)
        {
            if (o.Setup.Type == SetupType.None || o.Score < _o.RecordThreshold || o.Quality.Stale) continue;
            var key = $"{o.Symbol.Value}|{o.Setup.Type}";
            if (_lastRecorded.TryGetValue(key, out var last) && now - last < _o.DedupeWindow) continue;
            _lastRecorded[key] = now;
            var record = ToRecord(o, snapshot.Market);
            var outcome = new SignalOutcome(record.Id, null, null, null, null, null, 0, 0, record.Stop is null ? null : false, record.Target1 is null ? null : false, record.Target2 is null ? null : false, record.Target3 is null ? null : false, "none", null, now, false);
            var item = new SignalWithOutcome(record, outcome);
            _active[record.Id] = item;
            await _repo.SaveAsync(record, outcome, ct).ConfigureAwait(false);
            recorded.Add(record);
        }

        foreach (var (id, item) in _active.ToArray())
        {
            var opp = snapshot.Get(new Symbol(item.Signal.Symbol));
            if (opp is null || opp.Quality.Stale) continue;
            var updated = Advance(item, opp.Price, now, _o.Horizon);
            if (updated == item.Outcome) continue;
            var next = item with { Outcome = updated };
            if (updated.Complete) _active.TryRemove(id, out _); else _active[id] = next;
            await _repo.SaveAsync(next.Signal, updated, ct).ConfigureAwait(false);
        }
        return recorded;
    }

    public static SignalRecord ToRecord(Opportunity o, MarketContext market) => new(
        Guid.NewGuid(), o.Symbol.Value, o.At, o.Setup.Type.ToString(), o.Setup.Confidence.ToString(), o.Score,
        o.Breakdown.Components.ToDictionary(c => c.Name, c => c.Points), o.Breakdown.Penalties.Sum(p => p.Points),
        o.Price, o.Plan?.EntryMid, o.Plan?.Stop, o.Plan?.Target1, o.Plan?.Target2, o.Plan?.Target3, o.Plan?.RewardRatio1,
        market.Regime.ToString(), market.Btc?.Trend.ToString(), o.Overextension.DoNotChase, o.Breakdown.ConfigVersion);

    /// <summary>Pure outcome update for one price observation.</summary>
    public static SignalOutcome Advance(SignalWithOutcome item, double price, DateTimeOffset at, TimeSpan horizon)
    {
        var s = item.Signal;
        var oc = item.Outcome;
        if (price <= 0 || s.Price <= 0 || oc.Complete || at < s.At) return oc;
        var elapsed = at - s.At;
        var ret = price / s.Price - 1;
        var mfe = Math.Max(oc.Mfe, ret);
        var mae = Math.Min(oc.Mae, ret);

        var stopHit = oc.StopHit; var t1 = oc.Target1Hit; var t2 = oc.Target2Hit; var t3 = oc.Target3Hit; var first = oc.FirstEvent;
        if (s.Stop is { } stop && stopHit == false && price <= stop) { stopHit = true; if (first == "none") first = "stop"; }
        if (s.Target1 is { } tp1 && t1 == false && price >= tp1) { t1 = true; if (first == "none") first = "t1"; }
        if (s.Target2 is { } tp2 && t2 == false && price >= tp2) { t2 = true; if (first == "none") first = "t2"; }
        if (s.Target3 is { } tp3 && t3 == false && price >= tp3) { t3 = true; if (first == "none") first = "t3"; }

        var r5 = oc.Ret5m ?? (elapsed >= TimeSpan.FromMinutes(5) ? ret : null);
        var r15 = oc.Ret15m ?? (elapsed >= TimeSpan.FromMinutes(15) ? ret : null);
        var r30 = oc.Ret30m ?? (elapsed >= TimeSpan.FromMinutes(30) ? ret : null);
        var r60 = oc.Ret1h ?? (elapsed >= TimeSpan.FromMinutes(60) ? ret : null);
        var r120 = oc.Ret2h ?? (elapsed >= TimeSpan.FromMinutes(120) ? ret : null);
        var complete = elapsed >= horizon;

        double? r = oc.R;
        if (s.Stop is { } st && s.Entry is { } entry && entry > st)
        {
            var riskPerUnit = entry - st;
            if (first == "stop") r = -1;
            else if (first is "t1" or "t2" or "t3") r = s.RewardRatio1 ?? (s.Target1 is { } t ? (t - entry) / riskPerUnit : null);
            else if (complete) r = (price - entry) / riskPerUnit;
        }
        return oc with { Ret5m = r5, Ret15m = r15, Ret30m = r30, Ret1h = r60, Ret2h = r120, Mfe = mfe, Mae = mae, StopHit = stopHit, Target1Hit = t1, Target2Hit = t2, Target3Hit = t3, FirstEvent = first, R = r, LastPrice = at, Complete = complete };
    }

    /// <summary>
    /// Bar-based outcome update for replay: the stop is tested against the bar's low BEFORE targets are tested
    /// against its high, so a bar that touches both counts as a stop (pessimistic). Returns come from the close.
    /// </summary>
    public static SignalOutcome AdvanceBar(SignalWithOutcome item, double high, double low, double close, DateTimeOffset closeTime, TimeSpan horizon)
    {
        var s = item.Signal;
        var oc = item.Outcome;
        if (close <= 0 || s.Price <= 0 || oc.Complete || closeTime <= s.At) return oc;
        var elapsed = closeTime - s.At;
        var ret = close / s.Price - 1;
        var mfe = Math.Max(oc.Mfe, high / s.Price - 1);
        var mae = Math.Min(oc.Mae, low / s.Price - 1);

        var stopHit = oc.StopHit; var t1 = oc.Target1Hit; var t2 = oc.Target2Hit; var t3 = oc.Target3Hit; var first = oc.FirstEvent;
        if (s.Stop is { } stop && stopHit == false && low <= stop) { stopHit = true; if (first == "none") first = "stop"; }
        if (s.Target1 is { } tp1 && t1 == false && high >= tp1) { t1 = true; if (first == "none") first = "t1"; }
        if (s.Target2 is { } tp2 && t2 == false && high >= tp2) { t2 = true; if (first == "none") first = "t2"; }
        if (s.Target3 is { } tp3 && t3 == false && high >= tp3) { t3 = true; if (first == "none") first = "t3"; }

        var r5 = oc.Ret5m ?? (elapsed >= TimeSpan.FromMinutes(5) ? ret : null);
        var r15 = oc.Ret15m ?? (elapsed >= TimeSpan.FromMinutes(15) ? ret : null);
        var r30 = oc.Ret30m ?? (elapsed >= TimeSpan.FromMinutes(30) ? ret : null);
        var r60 = oc.Ret1h ?? (elapsed >= TimeSpan.FromMinutes(60) ? ret : null);
        var r120 = oc.Ret2h ?? (elapsed >= TimeSpan.FromMinutes(120) ? ret : null);
        var complete = elapsed >= horizon;

        double? r = oc.R;
        if (s.Stop is { } st && s.Entry is { } entry && entry > st)
        {
            var riskPerUnit = entry - st;
            if (first == "stop") r = -1;
            else if (first is "t1" or "t2" or "t3") r = s.RewardRatio1 ?? (s.Target1 is { } t ? (t - entry) / riskPerUnit : null);
            else if (complete) r = (close - entry) / riskPerUnit;
        }
        return oc with { Ret5m = r5, Ret15m = r15, Ret30m = r30, Ret1h = r60, Ret2h = r120, Mfe = mfe, Mae = mae, StopHit = stopHit, Target1Hit = t1, Target2Hit = t2, Target3Hit = t3, FirstEvent = first, R = r, LastPrice = closeTime, Complete = complete };
    }

    public static PerformanceReport Report(IReadOnlyList<SignalWithOutcome> items, DateTimeOffset at, int configVersion)
    {
        var overall = Bucket("all", items);
        var byScore = items.GroupBy(i => ScoreBucket(i.Signal.Score)).Select(g => Bucket(g.Key, g.ToList())).OrderByDescending(b => b.Key).ToList();
        var bySetup = items.GroupBy(i => i.Signal.Setup).Select(g => Bucket(g.Key, g.ToList())).OrderByDescending(b => b.Signals).ToList();
        var byRegime = items.GroupBy(i => i.Signal.Regime).Select(g => Bucket(g.Key, g.ToList())).OrderByDescending(b => b.Signals).ToList();
        var bySymbol = items.GroupBy(i => i.Signal.Symbol).Select(g => Bucket(g.Key, g.ToList())).OrderByDescending(b => b.Signals).Take(50).ToList();
        var byConfidence = items.GroupBy(i => i.Signal.Confidence).Select(g => Bucket(g.Key, g.ToList())).OrderByDescending(b => b.Key).ToList();
        var note = overall.Completed < 30 ? $"Only {overall.Completed} completed signals: too few to judge the model. Keep the scanner running." : "Statistics are descriptive; they measure this configuration on the data seen so far.";
        return new PerformanceReport(at, overall, byScore, bySetup, byRegime, bySymbol, byConfidence, configVersion, note);
    }

    public static string ScoreBucket(double score) => score >= 90 ? "90-100" : score >= 80 ? "80-89" : score >= 70 ? "70-79" : score >= 60 ? "60-69" : "<60";

    public static PerformanceBucket Bucket(string key, IReadOnlyList<SignalWithOutcome> items)
    {
        var completed = items.Where(i => i.Outcome.Complete).ToList();
        var withPlan = completed.Where(i => i.Signal.Stop is not null && i.Signal.Target1 is not null).ToList();
        var rs = completed.Where(i => i.Outcome.R is not null).Select(i => i.Outcome.R!.Value).ToList();
        var pos = rs.Where(r => r > 0).Sum();
        var neg = -rs.Where(r => r < 0).Sum();
        double? Avg(Func<SignalWithOutcome, double?> f) { var v = completed.Select(f).Where(x => x is not null).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : v.Average(); }
        // Horizon returns are known before completion; use every signal that has reached the horizon.
        double? Positive(Func<SignalWithOutcome, double?> f) { var v = items.Select(f).Where(x => x is not null).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : (double)v.Count(x => x > 0) / v.Count; }
        return new PerformanceBucket(key, items.Count, completed.Count, withPlan.Count,
            withPlan.Count == 0 ? null : (double)withPlan.Count(i => i.Outcome.FirstEvent is "t1" or "t2" or "t3") / withPlan.Count,
            withPlan.Count == 0 ? null : (double)withPlan.Count(i => i.Outcome.FirstEvent == "stop") / withPlan.Count,
            rs.Count == 0 ? null : rs.Average(), rs.Count == 0 ? null : rs.Average(), neg > 0 ? pos / neg : null,
            Avg(i => i.Outcome.Ret5m), Avg(i => i.Outcome.Ret15m), Avg(i => i.Outcome.Ret30m), Avg(i => i.Outcome.Ret1h), Avg(i => i.Outcome.Ret2h),
            Positive(i => i.Outcome.Ret15m), Positive(i => i.Outcome.Ret30m), Positive(i => i.Outcome.Ret1h), Positive(i => i.Outcome.Ret2h),
            completed.Count == 0 ? null : completed.Average(i => i.Outcome.Mfe), completed.Count == 0 ? null : completed.Average(i => i.Outcome.Mae));
    }
}

/// <summary>Hosts the tracker on the scanner's cycle.</summary>
public sealed class SignalTrackerService : BackgroundService
{
    private readonly SignalTracker _tracker;
    private readonly ScannerService _scanner;
    private readonly ILogger<SignalTrackerService> _logger;
    private int _busy;

    public SignalTrackerService(SignalTracker tracker, ScannerService scanner, ILogger<SignalTrackerService> logger)
    {
        _tracker = tracker; _scanner = scanner; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _tracker.LoadAsync(stoppingToken).ConfigureAwait(false);
        void OnSnapshot(ScannerSnapshot s) => _ = ObserveAsync(s, stoppingToken);
        _scanner.SnapshotPublished += OnSnapshot;
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { _scanner.SnapshotPublished -= OnSnapshot; }
    }

    private async Task ObserveAsync(ScannerSnapshot s, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { await _tracker.ObserveAsync(s, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Signal tracking failed"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
