using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingScanner.Api.Product;
using TradingScanner.Core.Market;
using TradingScanner.Signals.Scanner;
using TradingScanner.Signals.Tape;
using Xunit;
using static TradingScanner.Tests.Scanner.ScanFixtures;

namespace TradingScanner.Tests.Alerts;

public sealed class ProductAlertsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Reader : IScannerReader
    {
        public ScannerSnapshot? Latest { get; set; }
        public Opportunity? Get(Symbol symbol) => Latest?.Get(symbol);
        public MarketTape Tape { get; } = new();
    }
    private sealed class Sender : IProductPushSender
    {
        public int Calls { get; private set; }
        public PushSendResult Result { get; set; } = new(true, false, null);
        public Task<PushSendResult> SendAsync(CustomerPushDevice device, CustomerAlertRecord alert, DateTimeOffset now, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
    private sealed class CapturePushHandler : HttpMessageHandler, IHttpClientFactory
    {
        public byte[] Body { get; private set; } = [];
        public string? Encoding { get; private set; }
        public string? Authentication { get; private set; }
        public string? Ttl { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(ct);
            Encoding = request.Content.Headers.ContentEncoding.Single();
            Authentication = request.Headers.Authorization?.Scheme;
            Ttl = request.Headers.GetValues("TTL").Single();
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public Clock Clock { get; } = new();
        public Reader Reader { get; } = new();
        public Sender Sender { get; } = new();
        public IConfigurationRoot Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Product:MarketDataApproved"] = "true" }).Build();
        public DbContextOptions<ProductDbContext> DbOptions => new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(_connection).Options;
        public IOptions<ProductPushOptions> Push { get; } = Options.Create(new ProductPushOptions { Enabled = true, PublicKey = "configured-for-fake-sender", PrivateKey = "test", Subject = "mailto:admin@example.test" });
        public IOptions<ScannerOptions> Scanner { get; } = Options.Create(new ScannerOptions());
        public ProductDbContext Db() => new(DbOptions);
        public ProductAccess Access(ProductDbContext db) => new(db, Options.Create(new BillingOptions { PriceId = "price_test" }), Clock);
        public ProductAlertEvaluator Evaluator(ProductDbContext db) => new(db, Access(db), Scanner, Push, Configuration, Clock);
        public ProductPushDispatcher Dispatcher(ProductDbContext db) => new(db, Access(db), Sender, Push, Scanner, Configuration, Clock, Reader);
        public ProductAlertOutcomeObserver Observer(ProductDbContext db) => new(db, Configuration, Scanner, Clock);
        public async Task Initialize()
        {
            await _connection.OpenAsync();
            await using var db = Db();
            await db.Database.EnsureCreatedAsync();
            foreach (var id in new[] { "alice", "bob", "free" })
            {
                db.Users.Add(new ProductUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant() });
                db.Preferences.Add(new CustomerPreferences { UserId = id, PushEnabled = true, OnboardingComplete = true });
                db.Watchlist.Add(new WatchlistItem { UserId = id, Symbol = id == "bob" ? "BTC-USD" : "XRP-USD" });
                db.AlertRules.Add(new CustomerAlertRule { UserId = id, Name = "Watch", CreatedAt = Start });
                if (id != "free") db.Subscriptions.Add(new ProductSubscription { UserId = id, PriceId = "price_test", StripeSubscriptionId = "sub_" + id, Status = "active", LatestInvoicePaid = true, CurrentPeriodEnd = Start.AddDays(1) });
                db.PushDevices.Add(new CustomerPushDevice { UserId = id, EndpointHash = id, DeviceName = id, CreatedAt = Start, Endpoint = "https://fcm.googleapis.com/fcm/send/" + id });
            }
            await db.SaveChangesAsync();
            Reader.Latest = Snapshot(Start);
        }
        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private static Opportunity Opportunity(DateTimeOffset at, double price = 100, double score = 80, bool stale = false, string symbol = "XRP-USD") => new(
        new Symbol(symbol), at, price, 1, score, new ScoreBreakdown([], [], score, score, 1),
        new SetupClassification(SetupType.Breakout, Confidence.High, TrendBias.Bullish, ["Observed breakout"], null, 99),
        new TradePlan(99, 101, "Confirm the breakout", 98, 98, 110, 114, 120, 2, 5, 7, 10, ["Observed price levels"], 102, EntryState.InZone),
        new OverextensionAssessment(0, false, [], null, null, null, null, null, null, null), ["Observed momentum"], "Breaks below support", [],
        new OpportunityMetrics(null, null, null, null, null, null, 2, null, null, null, null, null, null, null, null, null, null, 2, 100_000_000, null, null, null, null, null),
        null, new DataQuality(stale, stale ? 60_000 : 0, true, "Kraken", "Kraken"));
    private static ScannerSnapshot Snapshot(DateTimeOffset at, double price = 100, double score = 80, bool stale = false) =>
        new(at, Market() with { At = at }, [Opportunity(at, price, score, stale)], 1, 1);

    [Fact]
    public async Task Evidence_cooldown_and_edge_survive_new_context_and_customers_are_isolated()
    {
        await using var fixture = new Fixture(); await fixture.Initialize();
        await using (var db = fixture.Db())
        {
            Assert.Equal(1, await fixture.Evaluator(db).EvaluateAsync(fixture.Reader.Latest));
            var alert = await db.AlertRecords.SingleAsync();
            Assert.Equal("alice", alert.UserId);
            Assert.Contains("takerFeeBps", alert.EvidenceJson);
            Assert.Contains("not a win probability", alert.EvidenceJson);
            Assert.Single(await db.PushDeliveries.ToListAsync());
        }
        fixture.Clock.Now = Start.AddSeconds(10);
        fixture.Reader.Latest = Snapshot(fixture.Clock.Now);
        await using (var restarted = fixture.Db()) Assert.Equal(0, await fixture.Evaluator(restarted).EvaluateAsync(fixture.Reader.Latest));
        fixture.Clock.Now = Start.AddHours(2); fixture.Reader.Latest = Snapshot(fixture.Clock.Now);
        await using (var restarted = fixture.Db()) Assert.Equal(0, await fixture.Evaluator(restarted).EvaluateAsync(fixture.Reader.Latest));
        // A genuinely observed false edge followed by a fresh match after cooldown can produce another record.
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(5);
        await using (var db = fixture.Db()) Assert.Equal(0, await fixture.Evaluator(db).EvaluateAsync(Snapshot(fixture.Clock.Now, score: 10)));
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(5);
        await using (var db = fixture.Db()) Assert.Equal(1, await fixture.Evaluator(db).EvaluateAsync(Snapshot(fixture.Clock.Now)));
    }

    [Fact]
    public async Task Stale_data_unapproved_markets_and_engine_cost_veto_do_not_issue_alerts()
    {
        await using var fixture = new Fixture(); await fixture.Initialize();
        await using var db = fixture.Db();
        Assert.Equal(0, await fixture.Evaluator(db).EvaluateAsync(Snapshot(Start, stale: true)));
        fixture.Configuration["Product:MarketDataApproved"] = "false";
        Assert.Equal(0, await fixture.Evaluator(db).EvaluateAsync(Snapshot(Start)));
        fixture.Configuration["Product:MarketDataApproved"] = "true";
        var alice = await db.Preferences.SingleAsync(x => x.UserId == "alice");
        alice.TakerFeeBps = 1000;
        await db.SaveChangesAsync();
        Assert.Equal(0, await fixture.Evaluator(db).EvaluateAsync(Snapshot(Start)));
        Assert.Empty(await db.AlertRecords.ToListAsync());
    }

    [Fact]
    public async Task Quiet_hours_record_evidence_without_queueing_a_late_notification()
    {
        await using var fixture = new Fixture(); await fixture.Initialize();
        await using var db = fixture.Db();
        var preferences = await db.Preferences.SingleAsync(x => x.UserId == "alice");
        preferences.QuietHoursEnabled = true; preferences.QuietHoursStart = "11:00"; preferences.QuietHoursEnd = "13:00";
        await db.SaveChangesAsync();
        Assert.Equal(1, await fixture.Evaluator(db).EvaluateAsync(fixture.Reader.Latest));
        Assert.Equal("quiet-hours", (await db.AlertRecords.SingleAsync()).Status);
        Assert.Empty(await db.PushDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Delivery_retry_persists_and_accepted_is_not_claimed_as_displayed()
    {
        await using var fixture = new Fixture(); await fixture.Initialize();
        await using (var db = fixture.Db()) await fixture.Evaluator(db).EvaluateAsync(fixture.Reader.Latest);
        fixture.Sender.Result = new(false, false, "Network failure", 10);
        await using (var db = fixture.Db()) await fixture.Dispatcher(db).DispatchAsync();
        await using (var db = fixture.Db())
        {
            var delivery = await db.PushDeliveries.SingleAsync();
            Assert.Equal("retry", delivery.State); Assert.Equal(1, delivery.Attempts);
        }
        fixture.Clock.Now = Start.AddSeconds(11); fixture.Reader.Latest = Snapshot(fixture.Clock.Now);
        fixture.Sender.Result = new(true, false, null);
        await using (var db = fixture.Db()) await fixture.Dispatcher(db).DispatchAsync();
        await using (var db = fixture.Db())
        {
            var delivery = await db.PushDeliveries.SingleAsync();
            Assert.Equal("accepted", delivery.State); Assert.Equal(2, delivery.Attempts);
        }
        await using (var db = fixture.Db()) await fixture.Dispatcher(db).DispatchAsync();
        Assert.Equal(2, fixture.Sender.Calls);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("plan-inactive")]
    [InlineData("stale-data")]
    [InlineData("rule-disabled")]
    [InlineData("watchlist-removed")]
    [InlineData("rule-changed")]
    public async Task Delivery_rechecks_expiry_entitlement_and_current_data(string reason)
    {
        await using var fixture = new Fixture(); await fixture.Initialize();
        await using (var db = fixture.Db()) await fixture.Evaluator(db).EvaluateAsync(fixture.Reader.Latest);
        if (reason == "expired") fixture.Clock.Now = Start.AddMinutes(6);
        if (reason == "stale-data") fixture.Reader.Latest = Snapshot(Start, stale: true);
        if (reason == "plan-inactive")
        {
            await using var db = fixture.Db();
            (await db.Subscriptions.SingleAsync(x => x.UserId == "alice")).Status = "past_due";
            await db.SaveChangesAsync();
        }
        if (reason is "rule-disabled" or "watchlist-removed" or "rule-changed")
        {
            await using var db = fixture.Db();
            if (reason == "rule-disabled") (await db.AlertRules.SingleAsync(x => x.UserId == "alice")).Enabled = false;
            if (reason == "watchlist-removed") await db.Watchlist.Where(x => x.UserId == "alice").ExecuteDeleteAsync();
            if (reason == "rule-changed") (await db.Preferences.SingleAsync(x => x.UserId == "alice")).ConditionsJson = "[]";
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Db()) await fixture.Dispatcher(db).DispatchAsync();
        await using (var db = fixture.Db()) Assert.Equal(reason, (await db.PushDeliveries.SingleAsync()).State);
        Assert.Equal(0, fixture.Sender.Calls);
    }

    [Theory]
    [InlineData(111, "target-observed")]
    [InlineData(97, "stop-observed")]
    public async Task Favorable_and_unfavorable_sampled_quotes_are_recorded_without_mutating_issue_evidence(double price, string expected)
    {
        await using var fixture = new Fixture(); await fixture.Initialize();
        string evidence;
        await using (var db = fixture.Db()) { await fixture.Evaluator(db).EvaluateAsync(fixture.Reader.Latest); evidence = (await db.AlertRecords.SingleAsync()).EvidenceJson; }
        fixture.Clock.Now = Start.AddMinutes(1);
        await using (var db = fixture.Db()) await fixture.Observer(db).ObserveAsync(Snapshot(fixture.Clock.Now, price));
        await using (var db = fixture.Db())
        {
            Assert.Equal(evidence, (await db.AlertRecords.SingleAsync()).EvidenceJson);
            var outcome = await db.AlertOutcomes.SingleAsync();
            Assert.Equal(expected, outcome.Status); Assert.True(outcome.DataGap); Assert.Equal(price, outcome.ObservedPrice);
        }
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/token", true)]
    [InlineData("https://web.push.apple.com/token", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/token", true)]
    [InlineData("http://fcm.googleapis.com/token", false)]
    [InlineData("https://127.0.0.1/token", false)]
    [InlineData("https://fcm.googleapis.com.evil.example/token", false)]
    [InlineData("https://user@fcm.googleapis.com/token", false)]
    [InlineData("https://fcm.googleapis.com:444/token", false)]
    [InlineData("https://internal.example/token", false)]
    public void Push_subscription_endpoint_is_constrained_to_known_https_push_providers(string endpoint, bool expected) => Assert.Equal(expected, PushEndpointPolicy.IsAllowed(endpoint));

    [Fact]
    public void Quiet_hours_handle_midnight_and_invalid_timezone_fails_closed()
    {
        var prefs = new CustomerPreferences { QuietHoursEnabled = true, QuietHoursStart = "22:00", QuietHoursEnd = "08:00", TimeZone = "UTC" };
        Assert.True(ProductAlertPolicy.InQuietHours(prefs, Start.AddHours(11)));
        Assert.True(ProductAlertPolicy.InQuietHours(prefs, Start.AddHours(-5)));
        Assert.False(ProductAlertPolicy.InQuietHours(prefs, Start));
        prefs.TimeZone = "bad/timezone";
        Assert.True(ProductAlertPolicy.InQuietHours(prefs, Start));
    }

    [Fact]
    public void Empty_explicit_setup_types_inherit_customer_categories_and_empty_preferences_monitor_nothing()
    {
        var rule = new CustomerAlertRule();
        var prefs = new CustomerPreferences { ConditionsJson = "[\"trend\"]" };
        Assert.True(ProductAlertPolicy.SetupSelected(SetupType.TrendPullback, rule, prefs));
        Assert.False(ProductAlertPolicy.SetupSelected(SetupType.Breakout, rule, prefs));
        prefs.ConditionsJson = "[]";
        Assert.False(ProductAlertPolicy.SetupSelected(SetupType.TrendPullback, rule, prefs));
        rule.SetupTypesJson = "[\"Reversal\"]";
        Assert.True(ProductAlertPolicy.SetupSelected(SetupType.Reversal, rule, prefs));
    }

    [Fact]
    public void Quiet_hours_use_the_browser_Iana_timezone_including_daylight_saving()
    {
        var prefs = new CustomerPreferences { QuietHoursEnabled = true, QuietHoursStart = "22:00", QuietHoursEnd = "08:00", TimeZone = "America/Chicago" };
        // September is daylight saving time: 12:00 UTC is 07:00 locally, and 17:00 UTC is noon.
        Assert.True(ProductAlertPolicy.InQuietHours(prefs, Start));
        Assert.False(ProductAlertPolicy.InQuietHours(prefs, Start.AddHours(5)));
    }

    [Fact]
    public async Task Actual_sender_encrypts_only_neutral_content_while_original_private_evidence_stays_in_database()
    {
        await using var fixture = new Fixture(); await fixture.Initialize();
        await using var db = fixture.Db();
        var rule = await db.AlertRules.SingleAsync(x => x.UserId == "alice");
        rule.Name = "Private strategy alpha";
        await db.SaveChangesAsync();
        await fixture.Evaluator(db).EvaluateAsync(fixture.Reader.Latest);
        var record = await db.AlertRecords.SingleAsync();
        var evidence = record.EvidenceJson;
        using var applicationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var application = applicationKey.ExportParameters(true);
        using var browserKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browser = browserKey.ExportParameters(false);
        static byte[] Public(ECParameters p) => [4, .. p.Q.X!, .. p.Q.Y!];
        static string Url64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var auth = RandomNumberGenerator.GetBytes(16);
        var push = Options.Create(new ProductPushOptions
        {
            Enabled = true, PublicKey = Url64(Public(application)), PrivateKey = Url64(application.D!), Subject = "mailto:operator@example.test",
        });
        var device = await db.PushDevices.SingleAsync(x => x.UserId == "alice");
        device.P256dh = Url64(Public(browser)); device.Auth = Url64(auth);
        using var capture = new CapturePushHandler();
        var result = await new ProductWebPushSender(capture, push).SendAsync(device, record, Start, default);
        Assert.True(result.Accepted); Assert.Equal("aes128gcm", capture.Encoding); Assert.Equal("vapid", capture.Authentication); Assert.Equal("300", capture.Ttl);

        // Decode the actual encrypted HTTP body as the receiving browser would (RFC 8291/8188).
        var salt = capture.Body[..16];
        var keyLength = capture.Body[20];
        var serverPublic = capture.Body[21..(21 + keyLength)];
        using var serverKey = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] } });
        var sharedSecret = browserKey.DeriveRawSecretAgreement(serverKey.PublicKey);
        byte[] info = [.. Encoding.ASCII.GetBytes("WebPush: info\0"), .. Public(browser), .. serverPublic];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, auth, info);
        var contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var encrypted = capture.Body[(21 + keyLength)..];
        var plaintext = new byte[encrypted.Length - 16];
        using (var aes = new AesGcm(contentKey, 16)) aes.Decrypt(nonce, encrypted[..^16], encrypted[^16..], plaintext);
        Assert.Equal(2, plaintext[^1]);
        var json = Encoding.UTF8.GetString(plaintext[..^1]);
        using var payload = JsonDocument.Parse(json);
        Assert.Equal("Stillwatch · monitoring update", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal(ProductAlertEndpoints.DetailUrl(record.Id), payload.RootElement.GetProperty("url").GetString());
        Assert.DoesNotContain(rule.Name, json); Assert.DoesNotContain(record.Symbol, json); Assert.DoesNotContain(record.SetupType, json);
        Assert.DoesNotContain("alice", json); Assert.DoesNotContain("price", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rule.Name, record.Message); Assert.Contains(record.Symbol, evidence);
        db.ChangeTracker.Clear();
        Assert.Equal(evidence, (await db.AlertRecords.SingleAsync()).EvidenceJson);
    }
}
