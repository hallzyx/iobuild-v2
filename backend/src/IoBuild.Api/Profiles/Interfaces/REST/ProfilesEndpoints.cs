using IoBuild.Api.CoreBusiness;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Profiles.Interfaces.REST;

public static class ProfilesEndpoints
{
    public static void MapProfilesEndpoints(this WebApplication app)
    {
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
    }
}
