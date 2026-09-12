using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.Product;

public static class ProductRegistration
{
    public static IServiceCollection AddProductAccounts(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.Configure<ProductOptions>(configuration.GetSection("Product"));
        services.Configure<BillingOptions>(configuration.GetSection("Product:Billing"));
        services.Configure<MailOptions>(configuration.GetSection("Product:Mail"));
        var options = configuration.GetSection("Product").Get<ProductOptions>() ?? new();
        var database = Path.GetFullPath(options.DatabasePath, environment.ContentRootPath);
        var keys = Path.GetFullPath(options.DataProtectionKeysPath, environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        Directory.CreateDirectory(keys);
        services.AddDbContext<ProductDbContext>(o => o.UseSqlite($"Data Source={database};Foreign Keys=True;Default Timeout=30"));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("TradingScanner.Product.v1");
        services.AddIdentityCore<ProductUser>(o =>
        {
            o.User.RequireUniqueEmail = true;
            o.Password.RequiredLength = 12;
            o.Password.RequireDigit = true;
            o.Password.RequireLowercase = true;
            o.Password.RequireUppercase = true;
            o.Password.RequireNonAlphanumeric = false;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddEntityFrameworkStores<ProductDbContext>().AddSignInManager().AddDefaultTokenProviders();
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "scanner.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromDays(7);
            o.SlidingExpiration = true;
            o.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return ctx.Response.WriteAsJsonAsync(new { error = "Sign in to continue." }); },
                OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return ctx.Response.WriteAsJsonAsync(new { error = "Access denied." }); },
                OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync,
            };
        });
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
        services.AddAuthorization();
        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.Name = "scanner.csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        services.AddScoped<ProductAccess>();
        services.AddScoped<ProductMailer>();
        services.AddSingleton<BillingMutex>();
        services.AddHttpClient<IStripeGateway, StripeGateway>(c => { c.BaseAddress = new Uri("https://api.stripe.com/v1/"); c.Timeout = TimeSpan.FromSeconds(20); });
        services.AddScoped<BillingService>();
        return services;
    }

    public static async Task InitializeProductDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        // Initial product schema. Future schema changes must use reviewed EF migrations, not EnsureCreated.
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    public static IApplicationBuilder UseProductProtection(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        var product = path.StartsWithSegments("/api/product");
        var options = context.RequestServices.GetRequiredService<IOptions<ProductOptions>>().Value;
        if (product)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method) && path != "/api/product/billing/webhook")
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "Refresh the page and retry. The security token is missing or expired." });
                    return;
                }
            }
        }
        else if (options.CommercialMode && (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs")))
        {
            // Legacy global alert/paper/stock APIs have no customer ownership model. Keep their code for
            // private deployments, but never expose those mutable stores in the subscription product.
            var readOnlyMarket = path.StartsWithSegments("/api/market") || path.StartsWithSegments("/api/scanner");
            if (!readOnlyMarket || !HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { error = "This legacy endpoint is unavailable in the customer product." });
                return;
            }
            var id = ProductAccess.UserId(context.User);
            if (id is null || !await context.RequestServices.GetRequiredService<ProductAccess>().IsProAsync(id, context.RequestAborted))
            {
                context.Response.StatusCode = id is null ? 401 : 403;
                await context.Response.WriteAsJsonAsync(new { error = "Pro is required. Explore /api/product/overview for the free preview." });
                return;
            }
            if (!options.MarketDataApproved)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { error = "Commercial market-data permissions are pending. A labeled example preview is available." });
                return;
            }
        }
        await next();
    });
}
