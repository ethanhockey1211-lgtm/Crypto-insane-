using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.Product;

public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record EmailRequest(string? Email);
public sealed record ConfirmEmailRequest(string? UserId, string? Token);
public sealed record ResetPasswordRequest(string? Email, string? Token, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record DeleteAccountRequest(string? Password);
public sealed record WatchlistRequest(string[]? Symbols);
public sealed record PreferencesRequest(string? Exchange, string? MarketScope, string[]? Conditions, bool PushEnabled,
    bool QuietHoursEnabled, string? QuietHoursStart, string? QuietHoursEnd, string? TimeZone,
    decimal MakerFeeBps, decimal TakerFeeBps, decimal SlippageBps, bool OnboardingComplete);

public static class ProductAccountEndpoints
{
    public static IEndpointRouteBuilder MapProductAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product").RequireRateLimiting("api");
        var auth = group.MapGroup("/auth").RequireRateLimiting("product-auth");
        auth.MapGet("/csrf", (HttpContext ctx, IAntiforgery csrf) => Results.Ok(new { token = csrf.GetAndStoreTokens(ctx).RequestToken }));
        auth.MapPost("/register", async (RegisterRequest request, ProductDbContext db, UserManager<ProductUser> users,
            SignInManager<ProductUser> signIn, ProductMailer mailer, IOptions<ProductOptions> options, IConfiguration configuration) =>
        {
            if (!ValidEmail(request.Email) || request.Password is not { Length: >= 12 and <= 128 } || request.DisplayName is { Length: > 80 })
                return Error("Use a valid email, a name up to 80 characters and a password of 12–128 characters.");
            await using var transaction = await db.Database.BeginTransactionAsync();
            var user = new ProductUser { UserName = request.Email!.Trim(), Email = request.Email.Trim(), DisplayName = request.DisplayName?.Trim() ?? "" };
            var result = await users.CreateAsync(user, request.Password);
            if (!result.Succeeded)
                return Error(result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal))
                    ? "Use a password of at least 12 characters with uppercase, lowercase and a number." : "Unable to create this account. Try signing in or recovering your account.");
            db.Preferences.Add(new CustomerPreferences { UserId = user.Id, Exchange = configuration["MarketData:Provider"]?.ToLowerInvariant() ?? "kraken" });
            db.Subscriptions.Add(new ProductSubscription { UserId = user.Id });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await signIn.SignInAsync(user, isPersistent: true);
            var sent = await SendConfirmation(user, users, mailer, options.Value);
            return Results.Ok(new { message = sent ? "Account created. Check your email to confirm your address." : "Account created. Email verification is unavailable until the operator configures delivery." });
        });
        auth.MapPost("/login", async (LoginRequest request, UserManager<ProductUser> users, SignInManager<ProductUser> signIn) =>
        {
            if (!ValidEmail(request.Email) || request.Password is not { Length: <= 128 }) return Error("Email or password is incorrect.", 401);
            var user = await users.FindByEmailAsync(request.Email!.Trim());
            if (user is null) return Error("Email or password is incorrect.", 401);
            var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: true, lockoutOnFailure: true);
            return result.Succeeded ? Results.Ok(new { message = "Signed in." }) : Error("Email or password is incorrect, or sign-in is temporarily locked. Try again later.", 401);
        });
        auth.MapPost("/logout", async (SignInManager<ProductUser> signIn) => { await signIn.SignOutAsync(); return Results.Ok(new { message = "Signed out." }); });
        auth.MapPost("/forgot-password", async (EmailRequest request, UserManager<ProductUser> users, ProductMailer mailer, IOptions<ProductOptions> options) =>
        {
            if (!mailer.Configured) return Error("Account recovery email is not configured. Contact the site operator.", 503);
            if (ValidEmail(request.Email) && await users.FindByEmailAsync(request.Email!.Trim()) is { } user)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var url = Link(options.Value, "reset-password", ("email", user.Email!), ("token", token));
                await mailer.SendLinkAsync(user.Email!, "Reset your scanner password", "Choose a new password for your account.", url);
            }
            return Results.Ok(new { message = "If the account exists, a recovery link has been sent." });
        });
        auth.MapPost("/reset-password", async (ResetPasswordRequest request, UserManager<ProductUser> users) =>
        {
            if (!ValidEmail(request.Email) || request.Token is not { Length: <= 4096 } || request.Password is not { Length: >= 12 and <= 128 }) return Error("Invalid or expired recovery link, or password does not meet requirements.");
            var user = await users.FindByEmailAsync(request.Email!.Trim());
            if (user is null) return Error("Invalid or expired recovery link, or password does not meet requirements.");
            var result = await users.ResetPasswordAsync(user, request.Token, request.Password);
            if (!result.Succeeded) return Error("Invalid or expired recovery link, or password does not meet requirements.");
            await users.UpdateSecurityStampAsync(user);
            return Results.Ok(new { message = "Password updated. Sign in with your new password." });
        });
        auth.MapPost("/confirm-email", async (ConfirmEmailRequest request, UserManager<ProductUser> users) =>
        {
            if (request.UserId is not { Length: <= 128 } || request.Token is not { Length: <= 4096 }) return Error("Invalid or expired confirmation link.");
            var user = await users.FindByIdAsync(request.UserId);
            if (user is null || !(await users.ConfirmEmailAsync(user, request.Token)).Succeeded) return Error("Invalid or expired confirmation link.");
            return Results.Ok(new { message = "Email confirmed." });
        });
        auth.MapPost("/resend-confirmation", async (HttpContext ctx, UserManager<ProductUser> users, ProductMailer mailer, IOptions<ProductOptions> options) =>
        {
            var user = (await users.GetUserAsync(ctx.User))!;
            if (!mailer.Configured) return Error("Email delivery is not configured. Contact the site operator.", 503);
            await SendConfirmation(user, users, mailer, options.Value);
            return Results.Ok(new { message = "A confirmation link has been requested." });
        }).RequireAuthorization();
        auth.MapPost("/change-password", async (ChangePasswordRequest request, HttpContext ctx, UserManager<ProductUser> users, SignInManager<ProductUser> signIn) =>
        {
            if (request.CurrentPassword is not { Length: <= 128 } || request.NewPassword is not { Length: >= 12 and <= 128 }) return Error("Check the current password and use a new password of 12–128 characters.");
            var user = (await users.GetUserAsync(ctx.User))!;
            if (!(await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword)).Succeeded) return Error("Check the current password and use uppercase, lowercase and a number in the new password.");
            await users.UpdateSecurityStampAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.Ok(new { message = "Password changed. Other sessions have been signed out." });
        }).RequireAuthorization();

        group.MapGet("/me", async (HttpContext ctx, ProductDbContext db, UserManager<ProductUser> users, ProductAccess access,
            IOptions<ProductOptions> options, IOptions<BillingOptions> billing, ProductMailer mailer, IConfiguration configuration) =>
        {
            var user = await users.GetUserAsync(ctx.User);
            var pro = user is not null && await access.IsProAsync(user.Id, ctx.RequestAborted);
            var subscription = user is null ? null : await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id);
            var preferences = user is null ? null : await db.Preferences.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
            var watchlist = user is null ? [] : await db.Watchlist.Where(x => x.UserId == user.Id).Select(x => x.Symbol).OrderBy(x => x).ToArrayAsync();
            return Results.Ok(new
            {
                authenticated = user is not null,
                user = user is null ? null : new { user.Id, user.Email, user.DisplayName, user.EmailConfirmed },
                preferences = preferences is null ? null : PreferenceView(preferences),
                watchlist,
                subscription = new { plan = pro ? "Pro" : "Free", status = subscription?.Status ?? "free", subscription?.CurrentPeriodEnd, cancelAtPeriodEnd = subscription?.CancelAtPeriodEnd ?? false },
                entitlements = new { scanner = pro, watchlist = pro, alerts = pro, backgroundPush = pro, watchlistLimit = pro ? options.Value.Limits.ProWatchlist : options.Value.Limits.FreeWatchlist, alertRuleLimit = pro ? options.Value.Limits.ProAlertRules : 0, historyDays = pro ? options.Value.Limits.AlertHistoryDays : 0 },
                capabilities = new { emailConfigured = mailer.Configured, billingConfigured = billing.Value.Configured, billingMode = "test", liveChargingEnabled = false,
                    supportedExchanges = new[] { configuration["MarketData:Provider"]?.ToLowerInvariant() ?? "kraken" }, supportedMarketScopes = new[] { "spot-usd" } },
            });
        });
        group.MapPut("/preferences", async (PreferencesRequest request, HttpContext ctx, ProductDbContext db, IConfiguration configuration) =>
        {
            var provider = configuration["MarketData:Provider"]?.ToLowerInvariant() ?? "kraken";
            if (request.Exchange != provider) return Error($"This deployment currently supports {provider} spot/USD.");
            if (request.Exchange is not ("kraken" or "coinbase") || request.MarketScope != "spot-usd" || request.Conditions is null || request.Conditions.Length > 3
                || request.Conditions.Any(x => x is not ("breakout" or "trend" or "momentum"))
                || !ValidTime(request.QuietHoursStart) || !ValidTime(request.QuietHoursEnd)
                || request.MakerFeeBps is < 0 or > 1000 || request.TakerFeeBps is < 0 or > 1000 || request.SlippageBps is < 0 or > 1000)
                return Error("Check exchange, supported conditions, quiet hours and fee assumptions (0–1,000 basis points).");
            try { TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone ?? ""); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return Error("Choose a valid time zone."); }
            var id = ProductAccess.UserId(ctx.User)!;
            var p = await db.Preferences.SingleAsync(x => x.UserId == id);
            p.Exchange = request.Exchange; p.MarketScope = request.MarketScope; p.ConditionsJson = JsonSerializer.Serialize(request.Conditions.Distinct());
            p.PushEnabled = request.PushEnabled; p.QuietHoursEnabled = request.QuietHoursEnabled; p.QuietHoursStart = request.QuietHoursStart!; p.QuietHoursEnd = request.QuietHoursEnd!;
            p.TimeZone = request.TimeZone!; p.MakerFeeBps = request.MakerFeeBps; p.TakerFeeBps = request.TakerFeeBps; p.SlippageBps = request.SlippageBps; p.OnboardingComplete = request.OnboardingComplete;
            await db.SaveChangesAsync();
            return Results.Ok(PreferenceView(p));
        }).RequireAuthorization();
        group.MapGet("/watchlist", async (HttpContext ctx, ProductDbContext db) =>
        {
            var id = ProductAccess.UserId(ctx.User)!;
            return Results.Ok(new { symbols = await db.Watchlist.Where(x => x.UserId == id).Select(x => x.Symbol).OrderBy(x => x).ToArrayAsync() });
        }).RequireAuthorization();
        group.MapPut("/watchlist", async (WatchlistRequest request, HttpContext ctx, ProductDbContext db, ProductAccess access, IOptions<ProductOptions> options) =>
        {
            var id = ProductAccess.UserId(ctx.User)!;
            var limit = await access.IsProAsync(id) ? options.Value.Limits.ProWatchlist : options.Value.Limits.FreeWatchlist;
            if (request.Symbols is null || request.Symbols.Length > limit || request.Symbols.Any(s => s is null || !Regex.IsMatch(s, "^[A-Z0-9]{1,20}-USD$", RegexOptions.CultureInvariant)))
                return Error($"Select up to {limit} USD spot market symbols, such as BTC-USD.");
            var symbols = request.Symbols.Distinct().ToArray();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Watchlist.Where(x => x.UserId == id).ExecuteDeleteAsync();
            db.Watchlist.AddRange(symbols.Select(symbol => new WatchlistItem { UserId = id, Symbol = symbol }));
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return Results.Ok(new { symbols });
        }).RequireAuthorization();
        group.MapDelete("/account", async ([Microsoft.AspNetCore.Mvc.FromBody] DeleteAccountRequest request, HttpContext ctx, UserManager<ProductUser> users,
            SignInManager<ProductUser> signIn, BillingService billing) =>
        {
            var user = (await users.GetUserAsync(ctx.User))!;
            if (request.Password is not { Length: <= 128 } || !await users.CheckPasswordAsync(user, request.Password)) return Error("Enter your current password to delete the account.", 400);
            try { await billing.CancelForDeletionAsync(user.Id, ctx.RequestAborted); }
            catch (BillingException ex) { return Error(ex.Message, 503); }
            if (!(await users.DeleteAsync(user)).Succeeded) return Error("Account deletion could not finish. Please try again.", 500);
            await signIn.SignOutAsync();
            return Results.Ok(new { message = "Account, preferences, devices, watchlist and alert records deleted. Payment-provider records may remain under its retention obligations." });
        }).RequireAuthorization();
        return app;
    }

    private static bool ValidEmail(string? email) => email is { Length: <= 254 } && MailAddress.TryCreate(email.Trim(), out var address) && address.Address == email.Trim();
    private static bool ValidTime(string? time) => time is not null && Regex.IsMatch(time, "^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);
    public static IResult Error(string error, int status = 400) => Results.Json(new { error }, statusCode: status);
    private static object PreferenceView(CustomerPreferences p) => new { p.Exchange, p.MarketScope, conditions = JsonSerializer.Deserialize<string[]>(p.ConditionsJson), p.PushEnabled, p.QuietHoursEnabled, p.QuietHoursStart, p.QuietHoursEnd, p.TimeZone, p.MakerFeeBps, p.TakerFeeBps, p.SlippageBps, p.OnboardingComplete };
    private static string Link(ProductOptions options, string action, params (string Key, string Value)[] values) =>
        options.PublicOrigin.TrimEnd('/') + "/app?auth=" + action + "&" + string.Join("&", values.Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value)}"));
    private static async Task<bool> SendConfirmation(ProductUser user, UserManager<ProductUser> users, ProductMailer mailer, ProductOptions options)
    {
        if (!mailer.Configured || user.EmailConfirmed) return false;
        return await mailer.SendLinkAsync(user.Email!, "Confirm your scanner email", "Confirm this email address for your scanner account.",
            Link(options, "confirm-email", ("userId", user.Id), ("token", await users.GenerateEmailConfirmationTokenAsync(user))));
    }
}
