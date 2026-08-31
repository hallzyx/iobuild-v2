using IoBuild.Api.CoreBusiness;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Subscriptions.Interfaces.REST;

public static class SubscriptionsEndpoints
{
    public static void MapSubscriptionsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/subscriptions", async (IoBuildDbContext db, CancellationToken ct) => Results.Ok(await db.Subscriptions.ToListAsync(ct)));
        app.MapGet("/api/v1/subscriptions/{id:int}", async (int id, IoBuildDbContext db, CancellationToken ct) => await db.Subscriptions.FindAsync([id], ct) is { } item ? Results.Ok(item) : Results.NotFound());
        app.MapPut("/api/v1/subscriptions/{id:int}", async (int id, CreateSubscriptionRequest request, IoBuildDbContext db, CancellationToken ct) => { var item = await db.Subscriptions.FindAsync([id], ct); if (item is null) return Results.NotFound(); item.PlanId = request.PlanId; item.EndDate = request.EndDate; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        app.MapPost("/api/v1/subscriptions/{id:int}/cancel", async (int id, IoBuildDbContext db, CancellationToken ct) => { var item = await db.Subscriptions.FindAsync([id], ct); if (item is null) return Results.NotFound(); item.Status = "cancelled"; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        app.MapPost("/api/v1/subscriptions/payments/sessions", async (PaymentCheckoutRequest request, IConfiguration configuration, IPaymentProvider provider, CancellationToken ct) =>
        {
            var restrictedKey = StripeRestrictedKeyResolver.Resolve(configuration);
            if (restrictedKey is null) return Results.Problem(statusCode: 503);
            var options = StripeIntegrationOptions.Create(restrictedKey);
            var session = await provider.CreateCheckoutSessionAsync(request, options, ct);
            return session is null ? Results.Problem(statusCode: 503) : Results.Created($"/api/v1/subscriptions/payments/sessions/{session.Id}", new { session.Id, session.Url, session.AmountInCents, options.UsesDynamicPaymentMethods });
        });
        app.MapPatch("/api/v1/subscriptions/payments/sessions/{sessionId}", async (string sessionId, IPaymentProvider provider, CancellationToken ct) =>
        {
            var confirmation = await provider.ConfirmSessionAsync(sessionId, ct);
            return confirmation is null ? Results.Problem(statusCode: 503) : Results.Ok(confirmation);
        });
        app.MapGet("/api/v1/subscriptions/payments/invoices", async (int builderId, IPaymentProvider provider, CancellationToken ct) =>
        {
            var invoices = await provider.GetInvoicesAsync(builderId, ct);
            return invoices is null ? Results.Problem(statusCode: 503) : Results.Ok(invoices);
        });
        app.MapPost("/api/v1/subscriptions", async (CreateSubscriptionRequest request, IoBuildDbContext db, CancellationToken ct) =>
        {
            var subscription = new Subscription { BuilderId = request.BuilderId, PlanId = request.PlanId, StartDate = request.StartDate, EndDate = request.EndDate };
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/subscriptions/{subscription.Id}", subscription);
        });
        app.MapPost("/api/v1/webhooks/stripe", async (HttpRequest request, StripeWebhookProcessor processor, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var eventId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            var eventType = document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType)) return Results.BadRequest();
            var signature = request.Headers["Stripe-Signature"].ToString();
            return await processor.ProcessAsync(new StripeWebhookRequest(eventId, eventType, payload, signature), ct)
                ? Results.Ok(new { received = true, eventId })
                : Results.Unauthorized();
        }).AllowAnonymous();
    }
}
