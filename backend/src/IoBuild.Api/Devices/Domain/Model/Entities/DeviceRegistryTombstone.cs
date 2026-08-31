namespace IoBuild.Api.Persistence;

public sealed class DeviceRegistryTombstone
{
    public int DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int PublishAttempts { get; set; }
}
