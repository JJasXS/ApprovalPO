namespace ApprovalPO.Options;

public class ApprovalOptions
{
    public const string SectionName = "Approval";

    /// <summary>Auth cookie sliding session length (hours).</summary>
    public int SessionHours { get; set; } = 2;

    /// <summary>PO amount at or above triggers extra confirm before approve.</summary>
    public decimal HighValueAmountThreshold { get; set; } = 5000m;

    public int OtpExpiryMinutes { get; set; } = 5;

    public int OtpRateLimitWindowMinutes { get; set; } = 15;

    public int OtpMaxSendsPerWindow { get; set; } = 3;

    public int OtpMaxVerifyFailures { get; set; } = 5;

    public int OtpLockoutMinutes { get; set; } = 15;
}
