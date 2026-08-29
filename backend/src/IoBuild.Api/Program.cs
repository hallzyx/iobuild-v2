using System.Text;
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

app.Run();

public partial class Program;
