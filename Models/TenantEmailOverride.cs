namespace ApprovalPO.Models;

/// <summary>Optional SMTP overrides from tenant AWS payload (<c>email</c> map / Dynamo).</summary>
public sealed class TenantEmailOverride
{
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    /// <summary>SMTP login / From address (tenant field <c>smtpSenderEmail</c>).</summary>
    public string? SmtpSenderEmail { get; set; }
    /// <summary>Secrets Manager id or ARN; password loaded at send time.</summary>
    public string? SmtpAppPasswordSecretRef { get; set; }
    /// <summary>When false, OTP email sending is skipped.</summary>
    public bool? OtpEnabled { get; set; }
}

/// <summary>Cached result of one tenant HTTP fetch (Firebird + optional email).</summary>
public sealed class TenantResolvedPayload
{
    public required string ConnectionString { get; init; }
    public TenantEmailOverride? Email { get; init; }
}
