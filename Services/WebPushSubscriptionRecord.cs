namespace ApprovalPO.Services;

public sealed class WebPushSubscriptionRecord
{
    public string UserEmail { get; set; } = "";

    public string Endpoint { get; set; } = "";

    public string P256dh { get; set; } = "";

    public string Auth { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
