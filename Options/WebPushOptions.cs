namespace ApprovalPO.Options;

/// <summary>VAPID keys and contact for Web Push (RFC 8291). Leave keys empty to disable registration and sending.</summary>
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>When false, the background worker does not poll or send pushes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Contact URI for VAPID, typically <c>mailto:ops@example.com</c>.</summary>
    public string Subject { get; set; } = "";

    /// <summary>URL-safe Base64 public VAPID key (unpadded).</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>URL-safe Base64 private VAPID key (unpadded).</summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>How often the server checks Firebird for new pending POs to broadcast. Clamped 30–600; default 60.</summary>
    public int PollSeconds { get; set; } = 60;

    public bool HasVapidKeys =>
        !string.IsNullOrWhiteSpace(Subject)
        && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey);

    /// <summary>Worker may send pushes (requires <see cref="HasVapidKeys"/> and <see cref="Enabled"/>).</summary>
    public bool IsConfigured => Enabled && HasVapidKeys;

    public int PollSecondsClamped => Math.Clamp(PollSeconds, 30, 600);
}
