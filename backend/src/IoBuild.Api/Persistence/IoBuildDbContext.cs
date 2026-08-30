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
