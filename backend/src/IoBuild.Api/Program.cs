using IoBuild.Api.Contracts;
using IoBuild.Api.Persistence;
using IoBuild.Api.Readiness;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("IoBuild")
    ?? "Server=localhost;Port=3306;Database=iobuild;User=root;Password=iobuild";

builder.Services.AddDbContext<IoBuildDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
builder.Services.AddSingleton<MigrationReadiness>();
builder.Services.AddScoped<IMigrationRunner, EfMigrationRunner>();

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Migrations:ApplyOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var coordinator = new MigrationStartupCoordinator(
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>(),
        scope.ServiceProvider.GetRequiredService<MigrationReadiness>());
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

app.MapGet("/health", (MigrationReadiness readiness) =>
    readiness.IsReady
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not-ready", reason = readiness.FailureReason }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/api/v1/contracts", () => Results.Ok(LegacyApiContractCatalog.All));

app.Run();

public partial class Program;
