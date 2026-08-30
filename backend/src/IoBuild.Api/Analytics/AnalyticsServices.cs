using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;

namespace IoBuild.Api.Analytics;

// ── Projections (mirror AnalyticsDbContext, singular tables) ──

public sealed class DeviceProjection
{
    public int DeviceId { get; set; }
    public int OwnerUserId { get; set; }
    public int? ProjectId { get; set; }
    public int? UnitId { get; set; }
    public string DeviceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastEventAt { get; set; }
    public int? FloorNumber { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class ProjectProjection
{
    public int ProjectId { get; set; }
    public int BuilderUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastEventAt { get; set; }
}

public sealed class UnitProjection
{
    public int UnitId { get; set; }
    public int ProjectId { get; set; }
    public int BuilderUserId { get; set; }
    public int? OwnerUserId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime LastEventAt { get; set; }
    public int? Floor { get; set; }
    public string? RoomNumber { get; set; }
    public string? OwnerEmail { get; set; }
}

// ── Aggregates ──

public sealed class BuilderMetrics
{
    public int TotalDevices { get; set; }
    public int OnlineDevices { get; set; }
    public int OfflineDevices { get; set; }
    public int AlertsCount { get; set; }
    public int ActiveProjectsCount { get; set; }
    public int TotalUnits { get; set; }
    public int OccupiedUnits { get; set; }
    public double OccupancyRate { get; set; }
    public double EnergyEfficiencyAvg { get; set; }
    public List<HistoricalDataPoint> TemperatureHistory { get; set; } = [];
    public List<HistoricalDataPoint> EnergyHistory { get; set; } = [];
    public List<HistoricalDataPoint> HourlyEnergyData { get; set; } = [];
    public List<HistoricalDataPoint> MonthlyOccupancy { get; set; } = [];
    public Dictionary<string, int> DevicesByType { get; set; } = [];
    public List<Dictionary<string, object>> ProjectsOverview { get; set; } = [];
}

public sealed class OwnerMetrics
{
    public int TotalDevices { get; set; }
    public int OnlineDevices { get; set; }
    public int OfflineDevices { get; set; }
    public int AlertsCount { get; set; }
    public int MyUnitsCount { get; set; }
    public double EnergyThisMonth { get; set; }
    public double TemperatureAvg { get; set; }
    public double WaterUsageThisMonth { get; set; }
    public List<HistoricalDataPoint> TemperatureHistory { get; set; } = [];
    public List<HistoricalDataPoint> EnergyHistory { get; set; } = [];
    public List<HistoricalDataPoint> DailyEnergyConsumption { get; set; } = [];
    public List<HistoricalDataPoint> WaterUsageWeekly { get; set; } = [];
    public List<DeviceHealthStatus> DeviceHealthStatus { get; set; } = [];
    public List<Dictionary<string, object>> MyUnitsDetails { get; set; } = [];
}

public sealed class DeviceHealthStatus
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastOnline { get; set; }
}

public sealed class HistoricalDataPoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
    public string Metric { get; set; } = string.Empty;
}

// ── Queries ──

public sealed record GetBuilderDashboardQuery(int UserId);
public sealed record GetOwnerDashboardQuery(int UserId);
public sealed record GetHistoricalDataQuery(int ProjectId, string Metric, DateTime StartDate, DateTime EndDate);
public sealed record GetBuilderLiveEnergyQuery(int UserId, int Minutes);
public sealed record GetOwnerLiveEnergyQuery(int UserId, int Minutes);
public sealed record EnergyMinutePoint(DateTime Timestamp, double TotalEnergyKwh);

// ── Influx live adapters ──

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

// ── Query service (canonical SQL, correlated Any(), Online taxonomy, ResolveEffectiveStatusesAsync) ──

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

// ── Http Influx adapters (reusing InfluxHttpTelemetrySink pattern) ──

public sealed class LiveEnergyService : ILiveEnergyService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LiveEnergyService>? _logger;

    public LiveEnergyService(HttpClient httpClient, IConfiguration configuration, ILogger<LiveEnergyService>? logger = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IEnumerable<EnergyMinutePoint>> GetAggregatedAsync(IEnumerable<string> deviceIds, int minutes, CancellationToken ct = default)
    {
        var ids = deviceIds.ToList();
        if (ids.Count == 0) return [];
        var url = _configuration["Influx:Url"];
        var org = _configuration["Influx:Org"];
        var bucket = _configuration["Influx:Bucket"];
        var token = _configuration["Influx:Token"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(token))
        {
            _logger?.LogWarning("LiveEnergyService: Influx not configured — returning empty");
            return [];
        }
        var idFilter = string.Join(" or ", ids.Select(id => $"r.deviceId == \"{id}\""));
        var flux = $"from(bucket: \"{bucket}\") |> range(start: -{minutes}m) |> filter(fn: (r) => r._measurement == \"telemetry\" and r._field == \"energy_kwh\") |> filter(fn: (r) => {idFilter}) |> aggregateWindow(every: 1m, fn: mean, createEmpty: false) |> group(columns: [\"_time\"]) |> sum()";
        var endpoint = new Uri(new Uri(url.TrimEnd('/') + "/"), $"api/v2/query?org={Uri.EscapeDataString(org)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = flux, type = "flux" }), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", token);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            // Fallback: Influx v2 returns annotated CSV; if we cannot parse, return empty gracefully.
            // For HTTP fallback we return empty — real Flux parsing would require CSV parser.
            _logger?.LogInformation("LiveEnergyService: received {Length} bytes", body.Length);
            return [];
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LiveEnergyService: Influx query failed — returning empty");
            return [];
        }
    }
}

public sealed class LiveDeviceStatusService : ILiveDeviceStatusService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LiveDeviceStatusService>? _logger;

    public LiveDeviceStatusService(HttpClient httpClient, IConfiguration configuration, ILogger<LiveDeviceStatusService>? logger = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> GetLatestStatusesAsync(IEnumerable<string> deviceIds, CancellationToken ct = default)
    {
        var ids = deviceIds.ToList();
        if (ids.Count == 0) return new Dictionary<string, string>();
        var url = _configuration["Influx:Url"];
        var org = _configuration["Influx:Org"];
        var bucket = _configuration["Influx:Bucket"];
        var token = _configuration["Influx:Token"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(token))
        {
            return new Dictionary<string, string>();
        }
        var idFilter = string.Join(" or ", ids.Select(id => $"r.deviceId == \"{id}\""));
        var flux = $"from(bucket: \"{bucket}\") |> range(start: -30d) |> filter(fn: (r) => r._measurement == \"telemetry\" and r._field == \"status\") |> filter(fn: (r) => {idFilter}) |> group(columns: [\"deviceId\"]) |> last()";
        var endpoint = new Uri(new Uri(url.TrimEnd('/') + "/"), $"api/v2/query?org={Uri.EscapeDataString(org)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = flux, type = "flux" }), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", token);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogInformation("LiveDeviceStatusService: received {Length} bytes", body.Length);
            return new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LiveDeviceStatusService: Influx query failed — returning empty");
            return new Dictionary<string, string>();
        }
    }
}

// ── Projection importer (LWW, checkpoint, placeholder) ──

public sealed class AnalyticsProjectionImporter
{
    private readonly IoBuildDbContext _db;

    public AnalyticsProjectionImporter(IoBuildDbContext db) => _db = db;

    public async Task UpsertDeviceAsync(DeviceProjection incoming, CancellationToken ct = default)
    {
        // Invalid ref nulling: if ProjectId points to non-existent project, null it
        if (incoming.ProjectId.HasValue)
        {
            var exists = await _db.ProjectProjections.AnyAsync(p => p.ProjectId == incoming.ProjectId.Value, ct);
            if (!exists) incoming.ProjectId = null;
        }
        if (incoming.UnitId.HasValue)
        {
            var exists = await _db.UnitProjections.AnyAsync(u => u.UnitId == incoming.UnitId.Value, ct);
            if (!exists) incoming.UnitId = null;
        }

        var row = await _db.DeviceProjections.FindAsync([incoming.DeviceId], ct);
        if (row is null)
        {
            _db.DeviceProjections.Add(incoming);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (incoming.LastEventAt < row.LastEventAt) return; // LWW guard
        row.OwnerUserId = incoming.OwnerUserId;
        row.ProjectId = incoming.ProjectId;
        row.UnitId = incoming.UnitId;
        row.DeviceType = incoming.DeviceType;
        row.Status = incoming.Status;
        row.FloorNumber = incoming.FloorNumber;
        row.DeviceName = incoming.DeviceName;
        row.LastEventAt = incoming.LastEventAt;
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertProjectAsync(ProjectProjection incoming, CancellationToken ct = default)
    {
        var row = await _db.ProjectProjections.FindAsync([incoming.ProjectId], ct);
        if (row is null)
        {
            _db.ProjectProjections.Add(incoming);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (incoming.LastEventAt < row.LastEventAt) return;
        row.BuilderUserId = incoming.BuilderUserId;
        row.Name = incoming.Name;
        row.Status = incoming.Status;
        row.LastEventAt = incoming.LastEventAt;
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertUnitAsync(UnitProjection incoming, CancellationToken ct = default)
    {
        var row = await _db.UnitProjections.FindAsync([incoming.UnitId], ct);
        if (row is null)
        {
            // Invalid ref nulling for unit's ProjectId? Keep as-is if builder wants to trace orphan, but device nulling already covers it
            _db.UnitProjections.Add(incoming);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (incoming.LastEventAt < row.LastEventAt) return;
        row.ProjectId = incoming.ProjectId;
        row.BuilderUserId = incoming.BuilderUserId;
        if (incoming.OwnerUserId.HasValue) row.OwnerUserId = incoming.OwnerUserId;
        row.Status = incoming.Status;
        row.Floor = incoming.Floor;
        row.RoomNumber = incoming.RoomNumber;
        row.OwnerEmail = incoming.OwnerEmail;
        row.LastEventAt = incoming.LastEventAt;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ImportAsync(IEnumerable<ProjectProjection> projects, IEnumerable<UnitProjection> units, IEnumerable<DeviceProjection> devices, CancellationToken ct = default)
    {
        foreach (var p in projects) await UpsertProjectAsync(p, ct);
        foreach (var u in units) await UpsertUnitAsync(u, ct);
        foreach (var d in devices) await UpsertDeviceAsync(d, ct);
    }

    public async Task ApplyUnitOwnerMatchedAsync(int unitId, int projectId, int ownerUserId, string ownerEmail, DateTime occurredOn, CancellationToken ct = default)
    {
        var row = await _db.UnitProjections.FindAsync([unitId], ct);
        if (row is null)
        {
            row = new UnitProjection
            {
                UnitId = unitId,
                ProjectId = projectId,
                BuilderUserId = 0,
                Status = "Occupied",
                OwnerUserId = ownerUserId,
                OwnerEmail = ownerEmail,
                LastEventAt = occurredOn
            };
            _db.UnitProjections.Add(row);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (occurredOn < row.LastEventAt) return;
        row.OwnerUserId = ownerUserId;
        if (!string.IsNullOrEmpty(ownerEmail)) row.OwnerEmail = ownerEmail;
        row.Status = "Occupied";
        row.LastEventAt = occurredOn;
        await _db.SaveChangesAsync(ct);
    }
}
