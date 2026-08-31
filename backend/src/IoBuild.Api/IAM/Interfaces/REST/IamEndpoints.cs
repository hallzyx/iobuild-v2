using IoBuild.Api.Iam;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.IAM.Interfaces.REST;

/// <summary>
/// IAM endpoints. Extracted from Program.cs for readability (pure move, no behavior change).
/// </summary>
public static class IamEndpoints
{
    public static void MapIamEndpoints(this WebApplication app)
    {
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
    }
}
