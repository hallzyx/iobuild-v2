using System.Text;
using System.Text.Json;
using IoBuild.Api.CoreBusiness;
using IoBuild.Api.Contracts;
using IoBuild.Api.Iam;
using IoBuild.Api.Devices;
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
builder.Services.AddSingleton<MqttDeviceTransport>();
builder.Services.AddSingleton<IDeviceMqttPublisher>(services => services.GetRequiredService<MqttDeviceTransport>());
builder.Services.AddHostedService(services => services.GetRequiredService<MqttDeviceTransport>());
builder.Services.AddHttpClient<IInfluxTelemetrySink, InfluxHttpTelemetrySink>();
builder.Services.AddScoped<DeviceCommandService>();
builder.Services.AddScoped<DeviceTelemetryService>();
builder.Services.AddScoped<DeviceRegistryService>();
builder.Services.AddHostedService<DeviceRegistryAnnouncer>();
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
app.MapGet("/api/v1/devices/types", () => Results.Ok(new
{
    deviceTypes = new object[]
{
    new { code = "SmartMeter", displayName = "Smart Meter", scope = "floor", controllableAttributes = Array.Empty<object>() },
    new { code = "WaterSensor", displayName = "Water Sensor", scope = "floor", controllableAttributes = Array.Empty<object>() },
    new { code = "SmokeDetector", displayName = "Smoke Detector", scope = "floor", controllableAttributes = Array.Empty<object>() },
    new { code = "AirConditioner", displayName = "Air Conditioner", scope = "unit", controllableAttributes = new object[] { new { name = "targetTemperature", type = "number", min = 16, max = 30, unit = "C", enumMembers = (string[]?)null }, new { name = "mode", type = "enum", min = (double?)null, max = (double?)null, unit = (string?)null, enumMembers = new[] { "cooling", "heating", "fan" } }, new { name = "power", type = "boolean", min = (double?)null, max = (double?)null, unit = (string?)null, enumMembers = (string[]?)null } } },
    new { code = "SmartLight", displayName = "Smart Light", scope = "unit", controllableAttributes = new object[] { new { name = "brightness", type = "number", min = 0, max = 100, unit = "%", enumMembers = (string[]?)null }, new { name = "power", type = "boolean", min = (double?)null, max = (double?)null, unit = (string?)null, enumMembers = (string[]?)null } } }
}
})).AllowAnonymous();
app.MapGet("/api/v1/devices", async (IoBuildDbContext db, CancellationToken ct) => Results.Ok((await db.Devices.OrderBy(device => device.Id).ToListAsync(ct)).Select(DeviceResponse.From))).RequireAuthorization();
app.MapGet("/api/v1/devices/{id:int}", async (int id, IoBuildDbContext db, CancellationToken ct) => await db.Devices.FindAsync([id], ct) is { } device ? Results.Ok(DeviceResponse.From(device)) : Results.NotFound()).RequireAuthorization();
app.MapPost("/api/v1/devices", async (CreateDeviceRequest request, System.Security.Claims.ClaimsPrincipal user, IoBuildDbContext db, DeviceRegistryService registry, CancellationToken ct) =>
{
    var ownerId = int.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.Sid)?.Value, out var id) ? id : 0;
    var isOwnerCustom = request.UnitId.HasValue;
    if (isOwnerCustom)
    {
        if (!string.Equals(user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, "Owner", StringComparison.Ordinal)) return Results.Json(new { error = "Only unit owners may add custom devices." }, statusCode: 403);
        if (!await db.UnitOwnerProjections.AnyAsync(item => item.UnitId == request.UnitId && item.OwnerUserId == ownerId, ct)) return Results.Json(new { error = $"You do not own unit {request.UnitId} or ownership has not yet propagated." }, statusCode: 403);
        var catalog = DeviceCatalog.Find(request.Type);
        if (catalog is null) return Results.BadRequest(new { error = $"Device type '{request.Type}' is not in the catalog. Please select a type from the available catalog." });
        if (catalog.Scope == "floor") return Results.BadRequest(new { error = $"Device type '{request.Type}' cannot be added to a unit. This type is designated for floor-level provisioning only." });
        if (await db.Devices.AnyAsync(item => item.ProjectId == request.ProjectId && item.UnitId == request.UnitId && item.Type == request.Type, ct)) return Results.Conflict(new { error = "A device of this type already exists in this unit." });
    }
    else if (!string.IsNullOrWhiteSpace(request.MacAddress) && await db.Devices.AnyAsync(item => item.MacAddress == request.MacAddress, ct)) return Results.Conflict(new { error = "A device with the same MAC address already exists." });
    var device = new Device { Name = request.Name, Type = request.Type, Location = request.Location, MacAddress = isOwnerCustom ? null : request.MacAddress, ProjectId = request.ProjectId, UnitId = request.UnitId, Source = isOwnerCustom ? "OwnerCustom" : null, Status = request.Status, OwnerId = ownerId };
    db.Devices.Add(device);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Conflict(new { error = isOwnerCustom ? "A device of this type already exists in this unit." : "A device with the same MAC address already exists." }); }
    await registry.AnnounceAsync(device, ct); return Results.Created($"/api/v1/devices/{device.Id}", DeviceResponse.From(device));
}).RequireAuthorization();
app.MapPut("/api/v1/devices/{id:int}", async (int id, CreateDeviceRequest request, IoBuildDbContext db, DeviceRegistryService registry, CancellationToken ct) => { var device = await db.Devices.FindAsync([id], ct); if (device is null) return Results.NotFound(); device.Name = request.Name; device.Type = request.Type; device.Location = request.Location; device.MacAddress = request.MacAddress; device.ProjectId = request.ProjectId; device.Status = request.Status; await db.SaveChangesAsync(ct); await registry.AnnounceAsync(device, ct); return Results.NoContent(); }).RequireAuthorization();
app.MapDelete("/api/v1/devices/{id:int}", async (int id, IoBuildDbContext db, DeviceRegistryService registry, CancellationToken ct) => { var device = await db.Devices.FindAsync([id], ct); if (device is null) return Results.NotFound(); registry.QueueTombstone(id); db.Devices.Remove(device); await db.SaveChangesAsync(ct); try { await registry.ReconcileAsync(ct); } catch (HttpRequestException) { } return Results.NoContent(); }).RequireAuthorization();
app.MapPost("/api/v1/devices/{id:int}/commands", async (int id, DeviceCommandRequest request, System.Security.Claims.ClaimsPrincipal user, DeviceCommandService commands, CancellationToken ct) => { var ownerId = int.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.Sid)?.Value, out var value) ? value : 0; var role = user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value; try { var command = await commands.SendAuthorizedAsync(id, ownerId, role, request.Attribute, request.Value, ct); return Results.Ok(new { deviceId = id, attribute = request.Attribute, value = request.Value, acceptedAt = command.IssuedAt }); } catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); } catch (UnauthorizedAccessException exception) { return Results.Json(new { error = exception.Message }, statusCode: 403); } catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); } }).RequireAuthorization();
app.MapPost("/api/v1/devices/telemetry", async (TelemetryMessage request, DeviceTelemetryService telemetry, CancellationToken ct) => await telemetry.IngestAsync(request, ct) ? Results.Ok(new { received = true }) : Results.NotFound()).AllowAnonymous();
app.MapPost("/api/v1/devices/telemetry/replay", async (DeviceTelemetryService telemetry, CancellationToken ct) => Results.Ok(new { replayed = await telemetry.ReplayInfluxAsync(ct) })).RequireAuthorization();
app.MapGet("/api/v1/devices/{id:int}/energy", async (int id, DateTimeOffset? from, DateTimeOffset? to, IoBuildDbContext db, CancellationToken ct) =>
{
    if (await db.Devices.FindAsync([id], ct) is null) return Results.NotFound(new { message = $"Device with ID {id} not found" });
    var start = from ?? DateTimeOffset.UtcNow.AddDays(-1); var end = to ?? DateTimeOffset.UtcNow;
    return Results.Ok(await db.DeviceTelemetry.Where(item => item.DeviceId == id && item.OccurredAt >= start && item.OccurredAt <= end).OrderBy(item => item.OccurredAt).Select(item => new { timestamp = item.OccurredAt, energyKwh = item.EnergyKwh, temperatureC = item.TemperatureC, voltageV = item.VoltageV }).ToListAsync(ct));
}).RequireAuthorization();
app.MapGet("/api/v1/devices/{id:int}/status", async (int id, IoBuildDbContext db, CancellationToken ct) =>
{
    if (await db.Devices.FindAsync([id], ct) is null) return Results.NotFound(new { message = $"Device with ID {id} not found" });
    var telemetry = await db.DeviceTelemetry.Where(item => item.DeviceId == id).OrderByDescending(item => item.OccurredAt).FirstOrDefaultAsync(ct);
    var shadow = await db.DeviceShadows.FindAsync([id], ct);
    var desired = shadow?.DesiredJson is { Length: > 0 } desiredJson ? JsonSerializer.Deserialize<JsonElement>(desiredJson) : default;
    return Results.Ok(new { deviceId = id, status = telemetry?.Status ?? "unknown", lastSeen = telemetry?.OccurredAt ?? DateTimeOffset.MinValue, temperatureC = telemetry?.TemperatureC ?? 0, voltageV = telemetry?.VoltageV ?? 0, desired });
}).RequireAuthorization();
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
public sealed record CreateDeviceRequest(string Name, string Type, string Location, string? MacAddress, int ProjectId, string Status, int? UnitId = null);
public sealed record DeviceCommandRequest(string Attribute, JsonElement Value);
public sealed record DeviceResponse(int Id, string Name, string Type, string Location, string? MacAddress, int ProjectId, string Status)
{
    public static DeviceResponse From(Device device) => new(device.Id, device.Name, device.Type, device.Location, device.MacAddress, device.ProjectId, device.Status);
}
