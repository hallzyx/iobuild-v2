using IoBuild.Api.Cutover;

namespace IoBuild.Api.Cutover.Interfaces.REST;

public static class CutoverEndpoints
{
    public static void MapCutoverEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/cutover/status", (CutoverReadiness readiness) => readiness.ShouldBlockWrites
            ? Results.Json(new { status = "frozen", reason = readiness.FailureReason }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new { status = "ready" }));
        app.MapPost("/api/v1/cutover/freeze", (System.Security.Claims.ClaimsPrincipal user, CutoverReadiness readiness) =>
        {
            var role = user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? user.FindFirst("role")?.Value ?? string.Empty;
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) && !string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "admin_required" }, statusCode: StatusCodes.Status403Forbidden);
            readiness.Freeze();
            return Results.Ok(new { status = "frozen" });
        }).RequireAuthorization();
        app.MapPost("/api/v1/cutover/stabilize", async (System.Security.Claims.ClaimsPrincipal user, ICutoverHarness harness) =>
        {
            var ok = await harness.StabilizeAsync(user);
            return ok ? Results.Ok(new { status = "ready" }) : Results.Json(new { error = "admin_required" }, statusCode: StatusCodes.Status403Forbidden);
        }).RequireAuthorization();
    }
}
