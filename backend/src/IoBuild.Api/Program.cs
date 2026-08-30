using System.Text;
using System.Text.Json;
using IoBuild.Api.CoreBusiness;
using IoBuild.Api.Contracts;
using IoBuild.Api.Iam;
using IoBuild.Api.Persistence;
using IoBuild.Api.Readiness;
using IoBuild.Api.Workflows;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("IoBuild") ?? "Server=localhost;Port=3306;Database=iobuild;User=root;Password=iobuild";
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "iobuild-development-secret-must-be-replaced-before-production";

builder.Services.AddDbContext<IoBuildDbContext>(options => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
builder.Services.AddSingleton<MigrationReadiness>();
builder.Services.AddScoped<IMigrationRunner, EfMigrationRunner>();
builder.Services.AddScoped<WorkflowExecutor>();
builder.Services.AddScoped<IIntegrationDispatchQueue, IntegrationDispatchQueue>();
builder.Services.AddScoped<IWorkflow<RegisterUser, int>, RegisterUserWorkflow>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton(new JwtTokenIssuer(jwtSecret));
builder.Services.AddScoped<IamService>();
builder.Services.AddScoped<CoreBusinessService>();
builder.Services.AddHttpClient<ICloudinaryUploader, CloudinaryHttpUploader>();
builder.Services.AddHttpClient<IPaymentProvider, StripeHttpPaymentProvider>();
builder.Services.AddScoped<ProfilePhotoWorkflow>();
builder.Services.AddScoped<StripeWebhookProcessor>(services => new StripeWebhookProcessor(
    services.GetRequiredService<IoBuildDbContext>(),
    builder.Configuration["Stripe:WebhookSecret"] ?? string.Empty));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : string.Empty;
            if (string.IsNullOrEmpty(token) || await context.HttpContext.RequestServices.GetRequiredService<IamService>().IsRevokedAsync(token)) context.Fail("The token has been revoked.");
        }
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Migrations:ApplyOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var coordinator = new MigrationStartupCoordinator(scope.ServiceProvider.GetRequiredService<IMigrationRunner>(), scope.ServiceProvider.GetRequiredService<MigrationReadiness>());
    await coordinator.ApplyAsync(app.Lifetime.ApplicationStopping);
}

app.Use(async (context, next) =>
{
    var readiness = context.RequestServices.GetRequiredService<MigrationReadiness>();
    if (context.Request.Path.StartsWithSegments("/api/v1") && readiness.ShouldBlockRequests)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "migration_readiness_failed" });
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", (MigrationReadiness readiness) => readiness.IsReady ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "not-ready", reason = readiness.FailureReason }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/api/v1/contracts", () => Results.Ok(LegacyApiContractCatalog.All));
app.MapPost("/api/v1/users", async (RegisterUser request, IamService iam, CancellationToken ct) => { await iam.RegisterAsync(request, ct); return Results.Created("/api/v1/users", new { message = "User created successfully." }); }).AllowAnonymous();
app.MapPost("/api/v1/sessions", async (SignIn request, IamService iam, CancellationToken ct) =>
{
    try { return Results.Created("/api/v1/sessions", await iam.SignInAsync(request, ct)); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
}).AllowAnonymous();
app.MapDelete("/api/v1/sessions/current", async (HttpRequest request, IamService iam, CancellationToken ct) =>
{
    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "No token provided." });
    await iam.RevokeAsync(header["Bearer ".Length..].Trim(), ct);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/v1/users", async (IoBuildDbContext db, CancellationToken ct) => Results.Ok(await db.IamUsers.OrderBy(user => user.Id).Select(user => new { user.Id, user.Email, user.Role }).ToListAsync(ct))).RequireAuthorization();
app.MapGet("/api/v1/projects", async (System.Security.Claims.ClaimsPrincipal user, IoBuildDbContext db, CancellationToken ct) =>
{
    var builderId = int.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.Sid)?.Value, out var id) ? id : 0;
    return Results.Ok(await db.Projects.Where(project => project.BuilderId == builderId).OrderBy(project => project.Id).ToListAsync(ct));
}).RequireAuthorization();
app.MapPost("/api/v1/projects", async (CreateProjectRequest request, CoreBusinessService service, CancellationToken ct) =>
{
    var project = await service.CreateProjectAsync(request.Name, request.Description, request.Location, request.TotalUnits, request.BuilderId, request.ImageUrl, ct);
    return Results.Created($"/api/v1/projects/{project.Id}", project);
}).RequireAuthorization();
app.MapGet("/api/v1/projects/{id:int}", async (int id, IoBuildDbContext db, CancellationToken ct) => await db.Projects.FindAsync([id], ct) is { } item ? Results.Ok(item) : Results.NotFound()).RequireAuthorization();
app.MapPut("/api/v1/projects/{id:int}", async (int id, CreateProjectRequest request, IoBuildDbContext db, CancellationToken ct) => { var item = await db.Projects.FindAsync([id], ct); if (item is null) return Results.NotFound(); item.Name = request.Name; item.Description = request.Description; item.Location = request.Location; item.TotalUnits = request.TotalUnits; item.ImageUrl = request.ImageUrl; await db.SaveChangesAsync(ct); return Results.NoContent(); }).RequireAuthorization();
app.MapDelete("/api/v1/projects/{id:int}", async (int id, IoBuildDbContext db, CancellationToken ct) => { var item = await db.Projects.FindAsync([id], ct); if (item is null) return Results.NotFound(); db.Projects.Remove(item); await db.SaveChangesAsync(ct); return Results.NoContent(); }).RequireAuthorization();
app.MapPost("/api/v1/projects/{id:int}/structure", async (int id, ProjectStructureRequest request, System.Security.Claims.ClaimsPrincipal user, IoBuildDbContext db, CancellationToken ct) =>
{
    if (!string.Equals(user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, "Builder", StringComparison.OrdinalIgnoreCase)) return Results.Json(new { error = "Only users with the Builder role may define project structure." }, statusCode: 403);
    if (request.Floors < 1 || request.UnitsPerFloor < 1) return Results.Json(new { error = "floors and unitsPerFloor must be at least 1." }, statusCode: 422);
    if (request.FloorNumbers?.Any(floor => floor < 1 || floor > request.Floors) == true) return Results.BadRequest(new { error = "floor reference is out of range." });
    var project = await db.Projects.FindAsync([id], ct); if (project is null) return Results.NotFound();
    if (project.StructureDefined) return Results.Conflict(new { error = "Project structure already defined." });
    project.StructureDefined = true; await db.SaveChangesAsync(ct);
    return Results.Created($"/api/v1/projects/{id}/structure", new { message = $"Project structure defined: {request.Floors} floor(s), {request.UnitsPerFloor} unit(s) per floor." });
}).RequireAuthorization();
app.MapGet("/api/v1/profiles", async (int? userId, IoBuildDbContext db, CancellationToken ct) =>
{
    var profiles = db.Profiles.AsQueryable();
    if (userId.HasValue) profiles = profiles.Where(profile => profile.UserId == userId.Value);
    return Results.Ok(await profiles.OrderBy(profile => profile.Id).ToListAsync(ct));
}).RequireAuthorization();
app.MapPut("/api/v1/profiles/{id:int}", async (int id, CreateProfileRequest request, IoBuildDbContext db, CancellationToken ct) => { var item = await db.Profiles.FindAsync([id], ct); if (item is null) return Results.NotFound(); item.Name = request.Name; item.Username = request.Username; await db.SaveChangesAsync(ct); return Results.Ok(item); }).RequireAuthorization();
app.MapPatch("/api/v1/profiles/{userId:int}/photo", async (int userId, ReplaceProfilePhotoRequest request, ProfilePhotoWorkflow workflow, CancellationToken ct) => await workflow.ReplaceAsync(userId, request.ExpectedReference, request.Content, ct) ? Results.NoContent() : Results.Conflict()).RequireAuthorization();
app.MapPost("/api/v1/profiles", async (CreateProfileRequest request, CoreBusinessService service, CancellationToken ct) =>
{
    var profile = await service.CreateProfileAsync(request.UserId, request.Name, request.Username, ct);
    return Results.Created($"/api/v1/profiles/{profile.Id}", profile);
}).RequireAuthorization();
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
    using var document = JsonDocument.Parse(payload);
    var eventId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    var eventType = document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
    if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType)) return Results.BadRequest();
    var signature = request.Headers["Stripe-Signature"].ToString();
    return await processor.ProcessAsync(new StripeWebhookRequest(eventId, eventType, payload, signature), ct)
        ? Results.Ok(new { received = true, eventId })
        : Results.Unauthorized();
}).AllowAnonymous();

app.Run();

public partial class Program;

public sealed record CreateProjectRequest(string Name, string Description, string Location, int TotalUnits, int BuilderId, string? ImageUrl);
public sealed record CreateProfileRequest(int UserId, string Name, string Username);
public sealed record CreateSubscriptionRequest(int BuilderId, int PlanId, DateTimeOffset StartDate, DateTimeOffset? EndDate);
public sealed record ReplaceProfilePhotoRequest(string ExpectedReference, string Content);
public sealed record ProjectStructureRequest(int Floors, int UnitsPerFloor, List<int>? FloorNumbers);
