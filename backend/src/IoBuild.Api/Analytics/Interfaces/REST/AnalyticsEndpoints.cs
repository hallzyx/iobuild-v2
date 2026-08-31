using IoBuild.Api.Analytics;

namespace IoBuild.Api.Analytics.Interfaces.REST;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/analytics/builders/{userId:int}/metrics", async (int userId, IAnalyticsQueryService analytics, CancellationToken ct) =>
        {
            var result = await analytics.Handle(new GetBuilderDashboardQuery(userId), ct);
            return result is null ? Results.NotFound(new { message = "No builder metrics found for the specified user." }) : Results.Ok(result);
        });
        app.MapGet("/api/v1/analytics/owners/{userId:int}/metrics", async (int userId, IAnalyticsQueryService analytics, CancellationToken ct) =>
        {
            var result = await analytics.Handle(new GetOwnerDashboardQuery(userId), ct);
            return result is null ? Results.NotFound(new { message = "No owner metrics found for the specified user." }) : Results.Ok(result);
        });
        app.MapGet("/api/v1/analytics/builders/{userId:int}/energy", async (int userId, int? minutes, IAnalyticsQueryService analytics, CancellationToken ct) =>
        {
            var result = await analytics.Handle(new GetBuilderLiveEnergyQuery(userId, Math.Clamp(minutes ?? 10, 1, 60)), ct);
            return Results.Ok(result.Select(point => new { timestamp = point.Timestamp, totalEnergyKwh = point.TotalEnergyKwh }));
        });
        app.MapGet("/api/v1/analytics/owners/{userId:int}/energy", async (int userId, int? minutes, IAnalyticsQueryService analytics, CancellationToken ct) =>
        {
            var result = await analytics.Handle(new GetOwnerLiveEnergyQuery(userId, Math.Clamp(minutes ?? 10, 1, 60)), ct);
            return Results.Ok(result.Select(point => new { timestamp = point.Timestamp, totalEnergyKwh = point.TotalEnergyKwh }));
        });
        app.MapGet("/api/v1/analytics/insights", async (int projectId, string? metric, DateTime? startDate, DateTime? endDate, IAnalyticsQueryService analytics, CancellationToken ct) =>
        {
            var start = startDate ?? DateTime.UtcNow.AddDays(-30);
            var end = endDate ?? DateTime.UtcNow;
            var result = await analytics.Handle(new GetHistoricalDataQuery(projectId, metric ?? "temperature", start, end), ct);
            return Results.Ok(result);
        });
    }
}
