using System.Text.Json;
using IoBuild.Api.Devices;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Devices.Interfaces.REST;

public static class DevicesEndpoints
{
    public static void MapDevicesEndpoints(this WebApplication app)
    {
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
    }
}
