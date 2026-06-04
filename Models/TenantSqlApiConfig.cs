namespace ApprovalPO.Models;

/// <summary>
/// Optional per-tenant SQL Accounting HTTP API config (AWS execute-api, SigV4-signed),
/// parsed from the tenant payload <c>sqlApi</c> section. Credentials may be inline
/// (<c>accessKey</c>/<c>secretKey</c>) and/or referenced via Secrets Manager
/// (<c>sqlApiCredentialsSecretRef</c>, resolved at call time).
/// </summary>
public sealed class TenantSqlApiConfig
{
    public string? Host { get; set; }
    public string? Region { get; set; }
    public string? Service { get; set; }
    public bool UseTls { get; set; } = true;
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    /// <summary>Secrets Manager id/ARN holding JSON with accessKey/secretKey (resolved at call time).</summary>
    public string? CredentialsSecretRef { get; set; }

    public bool HasInlineKeys =>
        !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>True when keys can plausibly be resolved (inline now, or a secret ref to read later).</summary>
    public bool CanResolveCredentials => HasInlineKeys || !string.IsNullOrWhiteSpace(CredentialsSecretRef);

    public string ResolvedHost => string.IsNullOrWhiteSpace(Host) ? "api.sql.my" : Host!.Trim().TrimEnd('/');
    public string ResolvedRegion => string.IsNullOrWhiteSpace(Region) ? "ap-southeast-1" : Region!.Trim();
    public string ResolvedService => string.IsNullOrWhiteSpace(Service) ? "execute-api" : Service!.Trim();
    public string BaseUrl => (UseTls ? "https://" : "http://") + ResolvedHost;
}
