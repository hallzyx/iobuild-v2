using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IoBuild.Api.Analytics;

public interface ILiveEnergyService
{
    Task<IEnumerable<EnergyMinutePoint>> GetAggregatedAsync(IEnumerable<string> deviceIds, int minutes, CancellationToken ct = default);
}

public interface ILiveDeviceStatusService
{
    Task<Dictionary<string, string>> GetLatestStatusesAsync(IEnumerable<string> deviceIds, CancellationToken ct = default);
}

public interface IAnalyticsQueryService
{
    Task<BuilderMetrics?> Handle(GetBuilderDashboardQuery query, CancellationToken ct = default);
    Task<OwnerMetrics?> Handle(GetOwnerDashboardQuery query, CancellationToken ct = default);
    Task<IEnumerable<HistoricalDataPoint>> Handle(GetHistoricalDataQuery query, CancellationToken ct = default);
    Task<IEnumerable<EnergyMinutePoint>> Handle(GetBuilderLiveEnergyQuery query, CancellationToken ct = default);
    Task<IEnumerable<EnergyMinutePoint>> Handle(GetOwnerLiveEnergyQuery query, CancellationToken ct = default);
}

public sealed class AnalyticsQueryService : IAnalyticsQueryService
{
    private static readonly HashSet<string> OnlineStatuses = new(StringComparer.OrdinalIgnoreCase) { "online", "active" };
    private static bool IsOnline(string? status) => OnlineStatuses.Contains((status ?? string.Empty).Trim());

    private readonly IoBuildDbContext _db;
    private readonly ILiveEnergyService _liveEnergyService;
    private readonly ILiveDeviceStatusService _liveDeviceStatusService;
    private readonly ILogger<AnalyticsQueryService>? _logger;

    public AnalyticsQueryService(IoBuildDbContext db, ILiveEnergyService liveEnergyService, ILiveDeviceStatusService liveDeviceStatusService, ILogger<AnalyticsQueryService>? logger = null)
    {
        _db = db;
        _liveEnergyService = liveEnergyService;
        _liveDeviceStatusService = liveDeviceStatusService;
        _logger = logger;
    }

    private async Task<Dictionary<int, string>> ResolveEffectiveStatusesAsync(IReadOnlyCollection<DeviceProjection> devices, CancellationToken ct = default)
    {
        if (devices.Count == 0) return new Dictionary<int, string>();
        var liveStatuses = await _liveDeviceStatusService.GetLatestStatusesAsync(devices.Select(d => d.DeviceId.ToString()), ct);
        return devices.ToDictionary(d => d.DeviceId, d => liveStatuses.GetValueOrDefault(d.DeviceId.ToString(), d.Status));
    }

    public async Task<BuilderMetrics?> Handle(GetBuilderDashboardQuery query, CancellationToken ct = default)
    {
        _logger?.LogInformation("Building builder dashboard for user {UserId}", query.UserId);

        var builderProjectIds = await _db.ProjectProjections.Where(p => p.BuilderUserId == query.UserId).Select(p => p.ProjectId).ToListAsync(ct);
        var activeProjectsCount = builderProjectIds.Count;

        var devices = await _db.DeviceProjections.Where(d => d.ProjectId != null && _db.ProjectProjections.Any(p => p.BuilderUserId == query.UserId && p.ProjectId == d.ProjectId!.Value)).ToListAsync(ct);
        var effectiveStatuses = await ResolveEffectiveStatusesAsync(devices, ct);
        var totalDevices = devices.Count;
        var onlineDevices = devices.Count(d => IsOnline(effectiveStatuses[d.DeviceId]));
        var offlineDevices = devices.Count(d => !IsOnline(effectiveStatuses[d.DeviceId]));
        var devicesByType = devices.GroupBy(d => d.DeviceType).ToDictionary(g => g.Key, g => g.Count());
        var units = await _db.UnitProjections.Where(u => u.BuilderUserId == query.UserId).ToListAsync(ct);
        var totalUnits = units.Count;
        var occupiedUnits = units.Count(u => u.Status.Equals("Occupied", StringComparison.OrdinalIgnoreCase));
        var occupancyRate = totalUnits > 0 ? (double)occupiedUnits / totalUnits * 100 : 0;
        var projects = await _db.ProjectProjections.Where(p => p.BuilderUserId == query.UserId).ToListAsync(ct);
        var projectsOverview = projects.Select(p =>
        {
            var pUnits = units.Where(u => u.ProjectId == p.ProjectId).ToList();
            var pOccupied = pUnits.Count(u => u.Status.Equals("Occupied", StringComparison.OrdinalIgnoreCase));
            var pTotal = pUnits.Count;
            var pDevices = devices.Count(d => d.ProjectId == p.ProjectId);
            return new Dictionary<string, object>
            {
                ["id"] = p.ProjectId,
                ["name"] = p.Name,
                ["status"] = p.Status,
                ["totalUnits"] = pTotal,
                ["occupiedUnits"] = pOccupied,
                ["occupancyRate"] = pTotal > 0 ? (double)pOccupied / pTotal * 100 : 0.0,
                ["deviceCount"] = pDevices
            };
        }).ToList<Dictionary<string, object>>();

        return new BuilderMetrics
        {
            TotalDevices = totalDevices,
            OnlineDevices = onlineDevices,
            OfflineDevices = offlineDevices,
            AlertsCount = 0,
            ActiveProjectsCount = activeProjectsCount,
            TotalUnits = totalUnits,
            OccupiedUnits = occupiedUnits,
            OccupancyRate = occupancyRate,
            EnergyEfficiencyAvg = 0,
            DevicesByType = devicesByType,
            ProjectsOverview = projectsOverview,
            TemperatureHistory = [],
            EnergyHistory = [],
            HourlyEnergyData = [],
            MonthlyOccupancy = []
        };
    }

    public async Task<OwnerMetrics?> Handle(GetOwnerDashboardQuery query, CancellationToken ct = default)
    {
        _logger?.LogInformation("Building owner dashboard for user {UserId}", query.UserId);

        var devices = await _db.DeviceProjections.Where(d => d.UnitId != null && _db.UnitProjections.Any(u => u.OwnerUserId == query.UserId && u.UnitId == d.UnitId!.Value)).ToListAsync(ct);
        var effectiveStatuses = await ResolveEffectiveStatusesAsync(devices, ct);
        var totalDevices = devices.Count;
        var onlineDevices = devices.Count(d => IsOnline(effectiveStatuses[d.DeviceId]));
        var offlineDevices = devices.Count(d => !IsOnline(effectiveStatuses[d.DeviceId]));
        var deviceHealthStatus = devices.Select(d => new DeviceHealthStatus
        {
            DeviceId = d.DeviceId,
            DeviceName = d.DeviceName ?? $"{d.DeviceType} #{d.DeviceId}",
            Type = d.DeviceType,
            Status = effectiveStatuses[d.DeviceId],
            LastOnline = d.LastEventAt
        }).ToList();
        var units = await _db.UnitProjections.Where(u => u.OwnerUserId == query.UserId).ToListAsync(ct);
        var myUnitsCount = units.Count;
        var projectNames = await _db.ProjectProjections.Where(p => _db.UnitProjections.Any(u => u.OwnerUserId == query.UserId && u.ProjectId == p.ProjectId)).ToDictionaryAsync(p => p.ProjectId, p => p.Name, ct);
        var myUnitsDetails = units.Select(u => new Dictionary<string, object>
        {
            ["unitId"] = u.UnitId,
            ["projectId"] = u.ProjectId,
            ["projectName"] = projectNames.GetValueOrDefault(u.ProjectId, "Unknown"),
            ["status"] = u.Status,
            ["floor"] = u.Floor ?? 0,
            ["roomNumber"] = u.RoomNumber ?? string.Empty
        }).ToList<Dictionary<string, object>>();

        return new OwnerMetrics
        {
            TotalDevices = totalDevices,
            OnlineDevices = onlineDevices,
            OfflineDevices = offlineDevices,
            AlertsCount = 0,
            MyUnitsCount = myUnitsCount,
            EnergyThisMonth = 0,
            TemperatureAvg = 0,
            WaterUsageThisMonth = 0,
            TemperatureHistory = [],
            EnergyHistory = [],
            DailyEnergyConsumption = [],
            WaterUsageWeekly = [],
            DeviceHealthStatus = deviceHealthStatus,
            MyUnitsDetails = myUnitsDetails
        };
    }

    public Task<IEnumerable<HistoricalDataPoint>> Handle(GetHistoricalDataQuery query, CancellationToken ct = default)
    {
        _logger?.LogInformation("GetHistoricalData called for project {ProjectId} — telemetry out of scope, returning empty", query.ProjectId);
        return Task.FromResult<IEnumerable<HistoricalDataPoint>>([]);
    }

    public async Task<IEnumerable<EnergyMinutePoint>> Handle(GetBuilderLiveEnergyQuery query, CancellationToken ct = default)
    {
        _logger?.LogInformation("GetBuilderLiveEnergy for user {UserId}, {Minutes}m", query.UserId, query.Minutes);
        var deviceIds = await _db.DeviceProjections.Where(d => d.ProjectId != null && _db.ProjectProjections.Any(p => p.BuilderUserId == query.UserId && p.ProjectId == d.ProjectId!.Value)).Select(d => d.DeviceId.ToString()).ToListAsync(ct);
        if (deviceIds.Count == 0) return [];
        try { return await _liveEnergyService.GetAggregatedAsync(deviceIds, query.Minutes, ct); }
        catch (Exception) { return []; }
    }

    public async Task<IEnumerable<EnergyMinutePoint>> Handle(GetOwnerLiveEnergyQuery query, CancellationToken ct = default)
    {
        _logger?.LogInformation("GetOwnerLiveEnergy for user {UserId}, {Minutes}m", query.UserId, query.Minutes);
        var deviceIds = await _db.DeviceProjections.Where(d => d.UnitId != null && _db.UnitProjections.Any(u => u.OwnerUserId == query.UserId && u.UnitId == d.UnitId!.Value)).Select(d => d.DeviceId.ToString()).ToListAsync(ct);
        if (deviceIds.Count == 0) return [];
        try { return await _liveEnergyService.GetAggregatedAsync(deviceIds, query.Minutes, ct); }
        catch (Exception) { return []; }
    }
}
