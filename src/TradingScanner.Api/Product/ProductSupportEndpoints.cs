using Microsoft.EntityFrameworkCore;

namespace TradingScanner.Api.Product;

public sealed class CustomerSupportRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public string Status { get; set; } = "received";
    public DateTime CreatedAtUtc { get; set; }
}
public sealed record SupportRequest(string? Subject, string? Message);

public partial class ProductDbContext
{
    public DbSet<CustomerSupportRequest> SupportRequests => Set<CustomerSupportRequest>();
    partial void ConfigureSupportEntities(ModelBuilder builder)
    {
        builder.Entity<CustomerSupportRequest>().HasKey(x => x.Id);
        builder.Entity<CustomerSupportRequest>().HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        builder.Entity<CustomerSupportRequest>().HasOne<ProductUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public static class ProductSupportEndpoints
{
    public static IEndpointRouteBuilder MapProductSupportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product/support").RequireAuthorization().RequireRateLimiting("product-auth");
        group.MapGet("/", async (HttpContext http, ProductDbContext db, CancellationToken ct) =>
        {
            var id = ProductAccess.UserId(http.User)!;
            return Results.Ok(await db.SupportRequests.AsNoTracking().Where(x => x.UserId == id).OrderByDescending(x => x.CreatedAtUtc)
                .Take(20).Select(x => new { x.Id, x.Subject, x.Status, x.CreatedAtUtc }).ToListAsync(ct));
        });
        group.MapPost("/", async (SupportRequest request, HttpContext http, ProductDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 120 || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4000)
                return ProductAccountEndpoints.Error("Use a subject of 1–120 characters and a message of 1–4,000 characters. Do not include passwords or payment details.");
            var id = ProductAccess.UserId(http.User)!;
            var since = time.GetUtcNow().UtcDateTime.AddDays(-1);
            if (await db.SupportRequests.CountAsync(x => x.UserId == id && x.CreatedAtUtc >= since, ct) >= 5)
                return ProductAccountEndpoints.Error("You have reached the daily limit of five support requests. Your existing requests remain saved.", 429);
            var ticket = new CustomerSupportRequest { UserId = id, Subject = request.Subject.Trim(), Message = request.Message.Trim(), CreatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.SupportRequests.Add(ticket);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ticket.Id, ticket.Status, ticket.CreatedAtUtc, message = "Your request is saved for the site operator. A public response channel and response time are still pending setup; no email has been sent." });
        });
        return app;
    }
}
