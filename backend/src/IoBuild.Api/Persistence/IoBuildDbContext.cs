using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Persistence;

public sealed class IoBuildDbContext(DbContextOptions<IoBuildDbContext> options) : DbContext(options)
{
    public DbSet<FoundationRecord> FoundationRecords => Set<FoundationRecord>();
    public DbSet<IamUser> IamUsers => Set<IamUser>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();
    public DbSet<IntegrationDispatch> IntegrationDispatches => Set<IntegrationDispatch>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionWebhook> SubscriptionWebhooks => Set<SubscriptionWebhook>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceShadow> DeviceShadows => Set<DeviceShadow>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<DeviceTelemetry> DeviceTelemetry => Set<DeviceTelemetry>();
    public DbSet<TelemetryRecovery> TelemetryRecoveries => Set<TelemetryRecovery>();
    public DbSet<UnitOwnerProjection> UnitOwnerProjections => Set<UnitOwnerProjection>();
    public DbSet<DeviceRegistryTombstone> DeviceRegistryTombstones => Set<DeviceRegistryTombstone>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FoundationRecord>(entity =>
        {
            entity.ToTable("foundation_records");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Name).HasMaxLength(200).IsRequired();
        });
        modelBuilder.Entity<IamUser>(entity =>
        {
            entity.ToTable("iam_users");
            entity.HasKey(user => user.Id);
            entity.HasIndex(user => user.Email).IsUnique();
            entity.Property(user => user.Email).HasMaxLength(320).IsRequired();
            entity.Property(user => user.PasswordHash).HasMaxLength(255).IsRequired();
            entity.Property(user => user.Role).HasMaxLength(80).IsRequired();
        });
        modelBuilder.Entity<RevokedToken>(entity =>
        {
            entity.ToTable("iam_revoked_tokens");
            entity.HasKey(token => token.TokenHash);
            entity.Property(token => token.TokenHash).HasMaxLength(64);
        });
        modelBuilder.Entity<IntegrationDispatch>(entity =>
        {
            entity.ToTable("integration_dispatch");
            entity.HasKey(dispatch => dispatch.Id);
            entity.HasIndex(dispatch => dispatch.IdempotencyKey).IsUnique();
            entity.HasIndex(dispatch => new { dispatch.Status, dispatch.NextAttemptAt });
            entity.HasIndex(dispatch => new { dispatch.OrderingKey, dispatch.Sequence }).IsUnique();
            entity.Property(dispatch => dispatch.OwnerModule).HasMaxLength(80).IsRequired();
            entity.Property(dispatch => dispatch.Channel).HasMaxLength(80).IsRequired();
            entity.Property(dispatch => dispatch.OrderingKey).HasMaxLength(200).IsRequired();
            entity.Property(dispatch => dispatch.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(dispatch => dispatch.Payload).IsRequired();
            entity.Property(dispatch => dispatch.Status).HasConversion<string>().HasMaxLength(20);
        });
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Name).HasMaxLength(200).IsRequired();
            entity.Property(project => project.Description).HasMaxLength(2000).IsRequired();
            entity.Property(project => project.Location).HasMaxLength(500).IsRequired();
            entity.Property(project => project.ImageUrl).HasMaxLength(2000);
        });
        modelBuilder.Entity<Profile>(entity =>
        {
            entity.ToTable("profiles");
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => profile.UserId).IsUnique();
            entity.Property(profile => profile.Name).HasMaxLength(200).IsRequired();
            entity.Property(profile => profile.Username).HasMaxLength(100).IsRequired();
            entity.Property(profile => profile.PhotoReference).HasMaxLength(128);
            entity.Property(profile => profile.CloudinaryReference).HasMaxLength(2000);
        });
        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("subscriptions");
            entity.HasKey(subscription => subscription.Id);
            entity.HasIndex(subscription => new { subscription.BuilderId, subscription.PlanId, subscription.Status });
            entity.Property(subscription => subscription.Status).HasMaxLength(40).IsRequired();
        });
        modelBuilder.Entity<Device>(entity => { entity.ToTable("devices"); entity.HasKey(device => device.Id); entity.HasIndex(device => device.MacAddress).IsUnique(); entity.HasIndex(device => new { device.ProjectId, device.UnitId, device.Type }).IsUnique(); entity.Property(device => device.MacAddress).HasMaxLength(17); entity.Property(device => device.Source).HasMaxLength(30); entity.Property(device => device.Name).HasMaxLength(200).IsRequired(); entity.Property(device => device.Type).HasMaxLength(100).IsRequired(); entity.Property(device => device.Location).HasMaxLength(500).IsRequired(); entity.Property(device => device.Status).HasMaxLength(40).IsRequired(); });
        modelBuilder.Entity<DeviceShadow>(entity => { entity.ToTable("device_shadows"); entity.HasKey(shadow => shadow.DeviceId); entity.Property(shadow => shadow.DesiredJson).IsRequired(); });
        modelBuilder.Entity<DeviceCommand>(entity => { entity.ToTable("device_commands"); entity.HasKey(command => command.Id); entity.HasIndex(command => command.CommandId).IsUnique(); entity.HasIndex(command => command.DeviceId); entity.HasIndex(command => new { command.DeviceId, command.AcknowledgedAt, command.IssuedAt }); entity.Property(command => command.CommandId).HasMaxLength(64); entity.Property(command => command.DesiredJson).IsRequired(); });
        modelBuilder.Entity<DeviceTelemetry>(entity => { entity.ToTable("device_telemetry"); entity.HasKey(telemetry => telemetry.Id); entity.HasIndex(telemetry => telemetry.EventId).IsUnique(); entity.HasIndex(telemetry => new { telemetry.DeviceId, telemetry.OccurredAt }); entity.Property(telemetry => telemetry.EventId).HasMaxLength(128); entity.Property(telemetry => telemetry.Status).HasMaxLength(40); entity.Property(telemetry => telemetry.ReportedJson).IsRequired(); });
        modelBuilder.Entity<TelemetryRecovery>(entity => { entity.ToTable("telemetry_recovery"); entity.HasKey(recovery => recovery.Id); entity.HasIndex(recovery => recovery.EventId).IsUnique(); entity.Property(recovery => recovery.EventId).HasMaxLength(128); });
        modelBuilder.Entity<UnitOwnerProjection>(entity => { entity.ToTable("unit_owner_projections"); entity.HasKey(item => item.UnitId); entity.HasIndex(item => new { item.UnitId, item.OwnerUserId }).IsUnique(); });
        modelBuilder.Entity<DeviceRegistryTombstone>(entity => { entity.ToTable("device_registry_tombstones"); entity.HasKey(item => item.DeviceId); });
        modelBuilder.Entity<SubscriptionWebhook>(entity =>
        {
            entity.ToTable("subscription_webhooks");
            entity.HasKey(webhook => webhook.EventId);
            entity.Property(webhook => webhook.EventId).HasMaxLength(255);
            entity.Property(webhook => webhook.EventType).HasMaxLength(120).IsRequired();
        });
    }
}

public sealed class FoundationRecord { public int Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class IamUser { public int Id { get; set; } public string Email { get; set; } = string.Empty; public string PasswordHash { get; set; } = string.Empty; public string Role { get; set; } = string.Empty; }
public sealed class RevokedToken { public string TokenHash { get; set; } = string.Empty; public DateTimeOffset ExpiresAt { get; set; } public DateTimeOffset RevokedAt { get; set; } }
public enum DispatchStatus { Pending, InProgress, DeadLetter, Completed }
public sealed class IntegrationDispatch
{
    public long Id { get; set; }
    public string OwnerModule { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string OrderingKey { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DispatchStatus Status { get; set; } = DispatchStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class Project { public int Id { get; set; } public string Name { get; set; } = string.Empty; public string Description { get; set; } = string.Empty; public string Location { get; set; } = string.Empty; public int TotalUnits { get; set; } public int BuilderId { get; set; } public string? ImageUrl { get; set; } public bool StructureDefined { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class Profile { public int Id { get; set; } public int UserId { get; set; } public string Name { get; set; } = string.Empty; public string Username { get; set; } = string.Empty; public string? PhotoReference { get; set; } public string? CloudinaryReference { get; set; } }
public sealed class Subscription { public int Id { get; set; } public int BuilderId { get; set; } public int PlanId { get; set; } public string Status { get; set; } = "active"; public DateTimeOffset StartDate { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? EndDate { get; set; } }
public sealed class SubscriptionWebhook { public string EventId { get; set; } = string.Empty; public string EventType { get; set; } = string.Empty; public DateTimeOffset ReceivedAt { get; set; } }

public sealed class Device { public int Id { get; set; } public string Name { get; set; } = string.Empty; public string Type { get; set; } = string.Empty; public string Location { get; set; } = string.Empty; public string? MacAddress { get; set; } public int ProjectId { get; set; } public int? UnitId { get; set; } public int OwnerId { get; set; } public string? Source { get; set; } public string Status { get; set; } = "unknown"; }
public sealed class DeviceShadow { public int DeviceId { get; set; } public string DesiredJson { get; set; } = "{}"; public string? ReportedJson { get; set; } public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.MinValue; public long ShadowVersion { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class DeviceCommand { public long Id { get; set; } public int DeviceId { get; set; } public string CommandId { get; set; } = string.Empty; public string DesiredJson { get; set; } = "{}"; public DateTimeOffset IssuedAt { get; set; } public DateTimeOffset? PublishedAt { get; set; } public int PublishAttempts { get; set; } public DateTimeOffset? AcknowledgedAt { get; set; } }
public sealed class DeviceTelemetry { public long Id { get; set; } public int DeviceId { get; set; } public string EventId { get; set; } = string.Empty; public DateTimeOffset OccurredAt { get; set; } public string Status { get; set; } = string.Empty; public string ReportedJson { get; set; } = "{}"; public double EnergyKwh { get; set; } public double TemperatureC { get; set; } public double VoltageV { get; set; } public DateTimeOffset? InfluxWrittenAt { get; set; } }
public sealed class TelemetryRecovery { public long Id { get; set; } public string EventId { get; set; } = string.Empty; public DateTimeOffset CreatedAt { get; set; } }
public sealed class UnitOwnerProjection { public int UnitId { get; set; } public int OwnerUserId { get; set; } public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class DeviceRegistryTombstone { public int DeviceId { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset? PublishedAt { get; set; } public int PublishAttempts { get; set; } }
