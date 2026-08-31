using System.Text;
using System.Text.Json;
using IoBuild.Api.Analytics;
using IoBuild.Api.Analytics.Interfaces.REST;
using IoBuild.Api.CoreBusiness;
using IoBuild.Api.Contracts;
using IoBuild.Api.Cutover;
using IoBuild.Api.Cutover.Interfaces.REST;
using IoBuild.Api.Devices;
using IoBuild.Api.Devices.Interfaces.REST;
using IoBuild.Api.Iam;
using IoBuild.Api.IAM.Interfaces.REST;
using IoBuild.Api.Observability;
using IoBuild.Api.Persistence;
using IoBuild.Api.Profiles.Interfaces.REST;
using IoBuild.Api.Publishing.Interfaces.REST;
using IoBuild.Api.Readiness;
using IoBuild.Api.Subscriptions.Interfaces.REST;
using IoBuild.Api.Workflows;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("IoBuild") ?? "Server=localhost;Port=3306;Database=iobuild;User=root;Password=iobuild";
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "iobuild-development-secret-must-be-replaced-before-production";

builder.Services.AddDbContext<IoBuildDbContext>(options => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
builder.Services.AddSingleton<MigrationReadiness>();
builder.Services.AddSingleton<CutoverReadiness>();
builder.Services.AddScoped<ICutoverHarness, CutoverHarness>();
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
builder.Services.AddHttpClient<ILiveEnergyService, LiveEnergyService>();
builder.Services.AddHttpClient<ILiveDeviceStatusService, LiveDeviceStatusService>();
builder.Services.AddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddScoped<AnalyticsProjectionImporter>();
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
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("GatewayCorsPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
builder.Services.AddIoBuildObservability(builder.Configuration);
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

app.UseForwardedHeaders();
app.UseCors("GatewayCorsPolicy");

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
app.Use(async (context, next) =>
{
    var cutover = context.RequestServices.GetRequiredService<CutoverReadiness>();
    var isWrite = context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";
    var isCutoverControl = context.Request.Path.StartsWithSegments("/api/v1/cutover");
    if (context.Request.Path.StartsWithSegments("/api/v1") && isWrite && cutover.ShouldBlockWrites && !isCutoverControl)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "cutover_freeze_active" });
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

// ── Health & Contracts (Shared) ──
app.MapGet("/health", (MigrationReadiness readiness) => readiness.IsReady ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "not-ready", reason = readiness.FailureReason }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/api/v1/contracts", () => Results.Ok(LegacyApiContractCatalog.All));

// ── Per-BC endpoint maps (DDD Interfaces/REST) ──
// Each BC's endpoints are defined in its own Interfaces/REST/*Endpoints.cs.
// Program.cs stays thin — composition root only.
app.MapIamEndpoints();
app.MapPublishingEndpoints();
app.MapProfilesEndpoints();
app.MapSubscriptionsEndpoints();
app.MapDevicesEndpoints();
app.MapAnalyticsEndpoints();
app.MapCutoverEndpoints();

app.Run();

public partial class Program;

// ── Shared request/response DTOs (wire contracts, unchanged) ──
// Kept in Program.cs for minimal diff; in a full split they would live in
// each BC's Interfaces/REST/Resources/*. For now they remain global so that
// existing tests and frontend contracts stay green.

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
