using IoBuild.Api.Analytics;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();
var connectionString = configuration.GetConnectionString("IoBuild") ?? "Server=localhost;Port=3306;Database=iobuild;User=root;Password=iobuild";
var services = new ServiceCollection();
services.AddDbContext<IoBuildDbContext>(options => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
var provider = services.BuildServiceProvider();

var checkpointPath = args.Length > 0 ? args[0] : "checkpoint.json";
var legacyPath = args.Length > 1 ? args[1] : null;

Console.WriteLine($"IoBuild LegacyImporter — checkpoint: {checkpointPath}");

using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<IoBuildDbContext>();
var importer = new AnalyticsProjectionImporter(db);

var checkpoint = await CheckpointStore.LoadAsync(checkpointPath);
Console.WriteLine($"Checkpoint loaded: {checkpoint.LastImportAt:O}, counts: projects={checkpoint.ProjectCount}, units={checkpoint.UnitCount}, devices={checkpoint.DeviceCount}");

// If legacy JSON provided, import it; otherwise demonstrate idempotent seed import
if (legacyPath is not null && File.Exists(legacyPath))
{
    var json = await File.ReadAllTextAsync(legacyPath);
    var data = JsonSerializer.Deserialize<LegacyDump>(json) ?? new LegacyDump();
    var before = (await db.ProjectProjections.CountAsync(), await db.UnitProjections.CountAsync(), await db.DeviceProjections.CountAsync());
    await importer.ImportAsync(
        data.Projects.Select(p => new ProjectProjection { ProjectId = p.ProjectId, BuilderUserId = p.BuilderUserId, Name = p.Name, Status = p.Status, LastEventAt = p.LastEventAt }),
        data.Units.Select(u => new UnitProjection { UnitId = u.UnitId, ProjectId = u.ProjectId, BuilderUserId = u.BuilderUserId, OwnerUserId = u.OwnerUserId, Status = u.Status, LastEventAt = u.LastEventAt, Floor = u.Floor, RoomNumber = u.RoomNumber, OwnerEmail = u.OwnerEmail }),
        data.Devices.Select(d => new DeviceProjection { DeviceId = d.DeviceId, ProjectId = d.ProjectId, UnitId = d.UnitId, DeviceType = d.DeviceType, Status = d.Status, LastEventAt = d.LastEventAt, FloorNumber = d.FloorNumber, DeviceName = d.DeviceName }));
    var after = (await db.ProjectProjections.CountAsync(), await db.UnitProjections.CountAsync(), await db.DeviceProjections.CountAsync());
    Console.WriteLine($"Import done: projects {before.Item1}->{after.Item1}, units {before.Item2}->{after.Item2}, devices {before.Item3}->{after.Item3}");
    checkpoint.LastImportAt = DateTime.UtcNow;
    checkpoint.ProjectCount = after.Item1;
    checkpoint.UnitCount = after.Item2;
    checkpoint.DeviceCount = after.Item3;
    await CheckpointStore.SaveAsync(checkpointPath, checkpoint);
}
else
{
    // Demo: idempotent repeatability check without external file
    Console.WriteLine("No legacy file provided — verifying idempotent repeatability with empty import.");
    var before = (await db.ProjectProjections.CountAsync(), await db.UnitProjections.CountAsync(), await db.DeviceProjections.CountAsync());
    await importer.ImportAsync([], [], []);
    var after = (await db.ProjectProjections.CountAsync(), await db.UnitProjections.CountAsync(), await db.DeviceProjections.CountAsync());
    Console.WriteLine($"Repeatability check: {before} -> {after} (should be equal)");
}

// Handle pending UnitOwnerMatchedEvent placeholders (out-of-order delivery)
var pendingOwners = await db.UnitProjections.Where(u => u.OwnerUserId != null && u.Status != "Occupied").ToListAsync();
foreach (var unit in pendingOwners)
{
    await importer.ApplyUnitOwnerMatchedAsync(unit.UnitId, unit.ProjectId, unit.OwnerUserId!.Value, unit.OwnerEmail ?? string.Empty, unit.LastEventAt);
}
Console.WriteLine("LegacyImporter completed successfully.");

public sealed class CheckpointStore
{
    public DateTime LastImportAt { get; set; } = DateTime.MinValue;
    public int ProjectCount { get; set; }
    public int UnitCount { get; set; }
    public int DeviceCount { get; set; }

    public static async Task<CheckpointStore> LoadAsync(string path)
    {
        if (!File.Exists(path)) return new CheckpointStore();
        try { return JsonSerializer.Deserialize<CheckpointStore>(await File.ReadAllTextAsync(path)) ?? new CheckpointStore(); }
        catch { return new CheckpointStore(); }
    }

    public static async Task SaveAsync(string path, CheckpointStore checkpoint)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class LegacyDump
{
    public List<LegacyProject> Projects { get; set; } = [];
    public List<LegacyUnit> Units { get; set; } = [];
    public List<LegacyDevice> Devices { get; set; } = [];
}

public sealed class LegacyProject { public int ProjectId { get; set; } public int BuilderUserId { get; set; } public string Name { get; set; } = string.Empty; public string Status { get; set; } = string.Empty; public DateTime LastEventAt { get; set; } }
public sealed class LegacyUnit { public int UnitId { get; set; } public int ProjectId { get; set; } public int BuilderUserId { get; set; } public int? OwnerUserId { get; set; } public string Status { get; set; } = string.Empty; public DateTime LastEventAt { get; set; } public int? Floor { get; set; } public string? RoomNumber { get; set; } public string? OwnerEmail { get; set; } }
public sealed class LegacyDevice { public int DeviceId { get; set; } public int? ProjectId { get; set; } public int? UnitId { get; set; } public string DeviceType { get; set; } = string.Empty; public string Status { get; set; } = string.Empty; public DateTime LastEventAt { get; set; } public int? FloorNumber { get; set; } public string? DeviceName { get; set; } }
