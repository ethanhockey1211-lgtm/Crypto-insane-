using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingScanner.Signals.Scanner;

namespace TradingScanner.Api.Product;

public static class ProductAlertsRegistration
{
    public static IServiceCollection AddProductAlerts(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProductPushOptions>(configuration.GetSection("Product:Push"));
        services.AddHttpClient("product-webpush", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        // Push endpoint URLs are credentials; suppress HttpClient's default full-URL diagnostics.
        services.AddLogging(logging => logging.AddFilter("System.Net.Http.HttpClient.product-webpush", LogLevel.None));
        services.AddSingleton<IProductPushSender, ProductWebPushSender>();
        services.AddSingleton<ProductAlertWriteLock>();
        services.AddScoped<ProductAlertEvaluator>();
        services.AddScoped<ProductPushDispatcher>();
        services.AddScoped<ProductAlertOutcomeObserver>();
        services.AddHostedService<ProductAlertsWorker>();
        return services;
    }
}

public sealed class ProductAlertEvaluator(ProductDbContext db, ProductAccess access, IOptions<ScannerOptions> scannerOptions,
    IOptions<ProductPushOptions> pushOptions, IConfiguration configuration, TimeProvider time)
{
    public async Task<int> EvaluateAsync(ScannerSnapshot? snapshot, CancellationToken ct = default)
    {
        // Demonstration data must never generate customer market notifications.
        if (!configuration.GetValue<bool>("Product:MarketDataApproved") || snapshot is null) return 0;
        var now = time.GetUtcNow();
        var scanner = scannerOptions.Value;
        if (snapshot.At > now.AddSeconds(2) || now - snapshot.At > scanner.StaleQuoteThreshold) return 0;
        var rules = await db.AlertRules.Where(x => x.Enabled).ToListAsync(ct);
        if (rules.Count == 0) return 0;
        var ruleIds = rules.Select(x => x.Id).ToArray();
        var states = await db.AlertStates.Where(x => ruleIds.Contains(x.RuleId)).ToDictionaryAsync(x => (x.RuleId, x.Symbol), ct);
        var count = 0;
        // One shared snapshot; customer rules do not create exchange sockets or poll the exchange.
        foreach (var customerRules in rules.GroupBy(x => x.UserId))
        {
            var userId = customerRules.Key;
            if (!await access.IsProAsync(userId, ct)) continue;
            var preferences = await db.Preferences.SingleOrDefaultAsync(x => x.UserId == userId, ct);
            if (preferences is null || !preferences.OnboardingComplete || preferences.MarketScope != "spot-usd") continue;
            var watchlist = (await db.Watchlist.Where(x => x.UserId == userId).Select(x => x.Symbol).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var devices = await db.PushDevices.Where(x => x.UserId == userId && x.Enabled).ToListAsync(ct);
            foreach (var rule in customerRules)
            {
                var scope = AlertJson.Strings(rule.SymbolsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var types = AlertJson.Strings(rule.SetupTypesJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in snapshot.Opportunities)
                {
                    var symbol = raw.Symbol.Value;
                    if (!watchlist.Contains(symbol) || (scope.Count > 0 && !scope.Contains(symbol))
                        || !raw.Quality.Exchange.Equals(preferences.Exchange, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!states.TryGetValue((rule.Id, symbol), out var state))
                    {
                        state = new CustomerAlertState { RuleId = rule.Id, Symbol = symbol };
                        states.Add((rule.Id, symbol), state);
                        db.AlertStates.Add(state);
                    }
                    if (!ProductAlertPolicy.IsFresh(snapshot, raw, now, scanner.StaleQuoteThreshold))
                    {
                        // An outage interrupts continuous hold time, but is not an observed false edge.
                        state.HeldSince = null;
                        continue;
                    }
                    var opportunity = ProductMarketEndpoints.AssessForCustomer(raw, snapshot.Market, (double)preferences.TakerFeeBps, (double)preferences.SlippageBps, scanner, now);
                    var matches = opportunity.Execution?.Status == "Watch"
                        && opportunity.Score >= Math.Max(rule.MinimumScore, Math.Max(scanner.SetupScoreThreshold, scanner.Execution.MinScore))
                        && ProductAlertPolicy.SetupSelected(opportunity.Setup.Type, rule, preferences);
                    if (!ProductAlertPolicy.Advance(state, matches, snapshot.At, rule.HoldSeconds, rule.CooldownMinutes, scanner.StaleQuoteThreshold)) continue;

                    var status = ProductAlertPolicy.InQuietHours(preferences, now) ? "quiet-hours"
                        : !preferences.PushEnabled ? "push-disabled"
                        : !pushOptions.Value.IsConfigured ? "push-unconfigured"
                        : devices.Count == 0 ? "no-device" : "queued";
                    var record = new CustomerAlertRecord
                    {
                        UserId = userId, RuleId = rule.Id, RuleName = rule.Name, Symbol = symbol,
                        SetupType = opportunity.Setup.Type.ToString(), IssuedAt = now,
                        ExpiresAt = now.AddSeconds(Math.Clamp(pushOptions.Value.AlertExpirySeconds, 30, 900)), Status = status,
                        Message = $"{symbol}: {opportunity.Setup.Type} matched {rule.Name}. Conditional setup; verify the trigger and current data.",
                        EvidenceJson = AlertJson.Serialize(new
                        {
                            observedAt = snapshot.At, issuedAt = now, opportunity, market = snapshot.Market,
                            costAssumptions = new { preferences.MakerFeeBps, preferences.TakerFeeBps, preferences.SlippageBps, spreadBps = opportunity.Metrics.SpreadBps, basis = "Customer assumptions per side; estimates, not a fee quote or an executed order." },
                            execution = opportunity.Execution,
                            rule = new { rule.Name, rule.MinimumScore, rule.HoldSeconds, rule.CooldownMinutes, symbols = scope, setupTypes = types, inheritedCategories = types.Count == 0 ? AlertJson.Strings(preferences.ConditionsJson) : [] },
                            interpretation = "Observed prices and engine heuristics supporting a conditional setup. Score is not a win probability. No trade, fill, or investment return is recorded by this alert.",
                        }),
                    };
                    db.AlertRecords.Add(record);
                    if (opportunity.Plan is { } plan)
                        db.AlertOutcomes.Add(new CustomerAlertOutcome
                        {
                            AlertId = record.Id, Exchange = opportunity.Quality.Exchange, Symbol = symbol,
                            InitialPrice = opportunity.Price, TargetPrice = plan.Target1, StopPrice = plan.Stop,
                            LastObservationAt = snapshot.At,
                            WindowEndsAt = now.AddMinutes(Math.Clamp(pushOptions.Value.ObservationWindowMinutes, 5, 1440)),
                        });
                    if (status == "queued")
                        db.PushDeliveries.AddRange(devices.Select(device => new CustomerPushDelivery { AlertId = record.Id, DeviceId = device.Id, NextAttemptAt = now }));
                    count++;
                }
            }
        }
        // EF's transaction commits durable edge/cooldown state, immutable evidence, and the outbox together.
        await db.SaveChangesAsync(ct);
        return count;
    }
}

public sealed record PushSendResult(bool Accepted, bool PermanentFailure, string? Error, int? RetryAfterSeconds = null);
public interface IProductPushSender
{
    Task<PushSendResult> SendAsync(CustomerPushDevice device, CustomerAlertRecord alert, DateTimeOffset now, CancellationToken ct);
}

public sealed class ProductWebPushSender(IHttpClientFactory http, IOptions<ProductPushOptions> options) : IProductPushSender
{
    public async Task<PushSendResult> SendAsync(CustomerPushDevice device, CustomerAlertRecord alert, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.Value.IsConfigured) return new(false, false, "Server VAPID configuration is missing.");
        if (!PushEndpointPolicy.IsAllowed(device.Endpoint)) return new(false, true, "Unsupported or invalid push provider.");
        if (alert.ExpiresAt <= now) return new(false, true, "Alert expired before delivery.");
        try
        {
            var client = new PushServiceClient(http.CreateClient("product-webpush")) { AutoRetryAfter = false };
            using var authentication = new VapidAuthentication(options.Value.PublicKey, options.Value.PrivateKey) { Subject = options.Value.Subject };
            var subscription = new PushSubscription { Endpoint = device.Endpoint, Keys = new Dictionary<string, string> { ["p256dh"] = device.P256dh, ["auth"] = device.Auth } };
            // A browser subscription can outlive its login cookie. Keep lock-screen content neutral
            // so a shared browser/account switch cannot expose another customer's rules or markets.
            var payload = AlertJson.Serialize(new
            {
                title = alert.IsTest ? "Stillwatch · device test" : "Stillwatch · monitoring update",
                body = alert.IsTest ? "This is a device notification test. Delivery on other devices may differ."
                    : "A monitoring update is available. Sign in to review its timestamped evidence.",
                url = ProductAlertEndpoints.DetailUrl(alert.Id), tag = alert.Id.ToString("N"), expiresAt = alert.ExpiresAt,
            });
            var message = new PushMessage(payload)
            {
                TimeToLive = Math.Max(0, (int)(alert.ExpiresAt - now).TotalSeconds),
                Topic = alert.Id.ToString("N"),
                Urgency = PushMessageUrgency.Normal,
            };
            await client.RequestPushMessageDeliveryAsync(subscription, message, authentication, ct);
            // Accepted by the push service does not prove the device displayed or the customer read it.
            return new(true, false, null);
        }
        catch (PushServiceClientException ex)
        {
            var code = (int)ex.StatusCode;
            var gone = code is 404 or 410;
            var permanent = gone || (code >= 400 && code < 500 && code is not (408 or 429));
            var retry = ex.Headers?.RetryAfter?.Delta?.TotalSeconds
                ?? (ex.Headers?.RetryAfter?.Date is { } date ? (date - now).TotalSeconds : (double?)null);
            return new(false, permanent, gone ? "Subscription expired; enable notifications again on this device." : $"Push service returned HTTP {code}.",
                retry is { } seconds ? (int)Math.Clamp(seconds, 1, 3600) : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, false, "Push service request timed out."); }
        catch (HttpRequestException) { return new(false, false, "Push service network request failed."); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            // Never expose cryptographic material, subscription URL, or provider response bodies.
            return new(false, true, "Push credentials could not be used. Check server configuration or re-enable this device.");
        }
    }
}

public sealed class ProductPushDispatcher(ProductDbContext db, ProductAccess access, IProductPushSender sender,
    IOptions<ProductPushOptions> options, IOptions<ScannerOptions> scannerOptions, IConfiguration configuration, TimeProvider time, IScannerReader scanner)
{
    public async Task DispatchAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var due = await db.PushDeliveries.AsNoTracking().Where(x => (x.State == "queued" || x.State == "retry" || x.State == "sending") && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).Take(50).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in due)
        {
            now = time.GetUtcNow();
            // Durable lease supports recovery after a crash and prevents concurrent dispatch of one row.
            var claimed = await db.PushDeliveries.Where(x => x.Id == id && (x.State == "queued" || x.State == "retry" || x.State == "sending") && x.NextAttemptAt <= now)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, "sending").SetProperty(x => x.NextAttemptAt, now.AddMinutes(1)), ct);
            if (claimed == 0) continue;
            var delivery = await db.PushDeliveries.SingleAsync(x => x.Id == id, ct);
            var alert = await db.AlertRecords.SingleAsync(x => x.Id == delivery.AlertId, ct);
            var device = await db.PushDevices.SingleAsync(x => x.Id == delivery.DeviceId, ct);
            var preferences = await db.Preferences.SingleOrDefaultAsync(x => x.UserId == alert.UserId, ct);
            string? suppression = null;
            if (alert.ExpiresAt <= now) suppression = "expired";
            else if (!device.Enabled || device.UserId != alert.UserId) suppression = "device-disabled";
            else if (!await access.IsProAsync(alert.UserId, ct)) suppression = "plan-inactive";
            else if (!alert.IsTest)
            {
                if (!configuration.GetValue<bool>("Product:MarketDataApproved")) suppression = "market-data-unapproved";
                else if (preferences is null || !preferences.PushEnabled) suppression = "push-disabled";
                else if (ProductAlertPolicy.InQuietHours(preferences, now)) suppression = "quiet-hours";
                else
                {
                    var rule = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == alert.RuleId && x.UserId == alert.UserId && x.Enabled, ct);
                    var watched = await db.Watchlist.AnyAsync(x => x.UserId == alert.UserId && x.Symbol == alert.Symbol, ct);
                    var latest = scanner.Latest;
                    var current = latest?.Opportunities.FirstOrDefault(x => x.Symbol.Value == alert.Symbol);
                    if (rule is null) suppression = "rule-disabled";
                    else if (!watched) suppression = "watchlist-removed";
                    else if (latest is null || current is null || !ProductAlertPolicy.IsFresh(latest, current, now, scannerOptions.Value.StaleQuoteThreshold)) suppression = "stale-data";
                    else if ((AlertJson.Strings(rule.SymbolsJson) is { Length: > 0 } selected && !selected.Contains(alert.Symbol, StringComparer.OrdinalIgnoreCase))
                        || current.Score < rule.MinimumScore || !ProductAlertPolicy.SetupSelected(current.Setup.Type, rule, preferences)) suppression = "rule-changed";
                    else if (current.Setup.Type.ToString() != alert.SetupType
                        || !current.Quality.Exchange.Equals(preferences.Exchange, StringComparison.OrdinalIgnoreCase)) suppression = "setup-changed";
                    else if (ProductMarketEndpoints.AssessForCustomer(current, latest.Market, (double)preferences.TakerFeeBps, (double)preferences.SlippageBps, scannerOptions.Value, now).Execution?.Status != "Watch") suppression = "setup-invalidated";
                }
            }
            if (suppression is not null)
            {
                delivery.State = suppression;
                delivery.LastError = "Not sent: " + suppression + ".";
            }
            else if (!options.Value.IsConfigured)
            {
                delivery.State = "retry";
                delivery.LastError = "Push server is not configured.";
                delivery.NextAttemptAt = now.AddSeconds(30);
            }
            else
            {
                delivery.Attempts++;
                delivery.LastAttemptAt = now;
                var result = await sender.SendAsync(device, alert, now, ct);
                delivery.LastError = result.Error;
                if (result.Accepted)
                {
                    delivery.State = "accepted";
                    device.LastAcceptedAt = now;
                    device.LastError = null;
                }
                else
                {
                    device.LastError = result.Error;
                    var exhausted = delivery.Attempts >= Math.Clamp(options.Value.MaximumAttempts, 1, 10);
                    delivery.State = result.PermanentFailure || exhausted ? "failed" : "retry";
                    delivery.NextAttemptAt = now.AddSeconds(result.RetryAfterSeconds ?? Math.Min(120, 5 * Math.Pow(2, delivery.Attempts)));
                    if (result.Error?.StartsWith("Subscription expired", StringComparison.Ordinal) == true) device.Enabled = false;
                }
            }
            await db.SaveChangesAsync(ct);
            // Subsequent claims must not reuse tracked rows from before their lease update.
            db.ChangeTracker.Clear();
        }
    }
}

public sealed class ProductAlertsWorker(IServiceScopeFactory scopes, IScannerReader scanner, IOptions<ProductPushOptions> options,
    TimeProvider time, ILogger<ProductAlertsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 60)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using (var evaluation = scopes.CreateAsyncScope())
                    {
                        await evaluation.ServiceProvider.GetRequiredService<ProductAlertEvaluator>().EvaluateAsync(scanner.Latest, stoppingToken);
                        await evaluation.ServiceProvider.GetRequiredService<ProductAlertOutcomeObserver>().ObserveAsync(scanner.Latest, stoppingToken);
                    }
                    await using var dispatch = scopes.CreateAsyncScope();
                    await dispatch.ServiceProvider.GetRequiredService<ProductPushDispatcher>().DispatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Customer alert worker cycle failed ({ErrorType}); will retry next cycle.", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public sealed class ProductAlertOutcomeObserver(ProductDbContext db, IConfiguration configuration, IOptions<ScannerOptions> options, TimeProvider time)
{
    public async Task ObserveAsync(ScannerSnapshot? snapshot, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var pending = await db.AlertOutcomes.Where(x => x.Status == "monitoring").ToListAsync(ct);
        foreach (var outcome in pending)
        {
            if (now - outcome.LastObservationAt > options.Value.StaleQuoteThreshold) outcome.DataGap = true;
            if (outcome.WindowEndsAt <= now)
            {
                outcome.Status = "window-ended";
                outcome.ObservedAt = now;
                continue;
            }
            if (!configuration.GetValue<bool>("Product:MarketDataApproved") || snapshot is null) { outcome.DataGap = true; continue; }
            var quote = snapshot.Opportunities.FirstOrDefault(x => x.Symbol.Value == outcome.Symbol && x.Quality.Exchange.Equals(outcome.Exchange, StringComparison.OrdinalIgnoreCase));
            if (quote is null || !ProductAlertPolicy.IsFresh(snapshot, quote, now, options.Value.StaleQuoteThreshold)) { outcome.DataGap = true; continue; }
            if (snapshot.At <= outcome.LastObservationAt) continue;
            outcome.LastObservationAt = snapshot.At;
            if (quote.Price <= outcome.StopPrice) outcome.Status = "stop-observed";
            else if (quote.Price >= outcome.TargetPrice) outcome.Status = "target-observed";
            if (outcome.Status != "monitoring") { outcome.ObservedAt = snapshot.At; outcome.ObservedPrice = quote.Price; }
        }
        await db.SaveChangesAsync(ct);
    }
}
