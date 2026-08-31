namespace IoBuild.Api.Persistence;

public sealed class SubscriptionWebhook
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
}
