using IoBuild.Api.CoreBusiness;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Publishing.Interfaces.REST;

public static class PublishingEndpoints
{
    public static void MapPublishingEndpoints(this WebApplication app)
    {
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
    }
}
