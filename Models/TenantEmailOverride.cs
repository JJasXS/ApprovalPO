namespace ApprovalPO.Models;

/// <summary>Optional SMTP overrides from tenant AWS payload: <c>email</c> base + <c>proaccEmail</c> overlay.</summary>
public sealed class TenantEmailOverride
{
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    /// <summary>SMTP login / From address (tenant field <c>smtpSenderEmail</c>).</summary>
    public string? SmtpSenderEmail { get; set; }
    /// <summary>Secrets Manager id or ARN; password loaded at send time.</summary>
    public string? SmtpAppPasswordSecretRef { get; set; }
    /// <summary>Secrets Manager JSON with host, port, user, password (same as ProAccScanner).</summary>
    public string? SmtpCredentialsSecretRef { get; set; }
    /// <summary>When false, OTP email sending is skipped.</summary>
    public bool? OtpEnabled { get; set; }
}

/// <summary>Cached result of one tenant HTTP fetch (Firebird + optional email).</summary>
public sealed class TenantResolvedPayload
{
    public required string ConnectionString { get; init; }
    public TenantEmailOverride? Email { get; init; }
    public TenantDashboardModules? DashboardModules { get; init; }
    public TenantDashboardModules? AdminDashboardModules { get; init; }
    public TenantDashboardModules? MaintenanceDashboardModules { get; init; }
    /// <summary>Per-user role allowlists (Admin vs Maintenance). Null = legacy mode (treat everyone as Admin).</summary>
    public TenantUserRoles? UserRoles { get; init; }
    /// <summary>Secrets Manager id/ARN holding the OpenAI API key (tenant field <c>openai.openaiApiKeySecretRef</c>).</summary>
    public string? OpenAiApiKeySecretRef { get; init; }
}

/// <summary>
/// Optional per-tenant dashboard module visibility flags.
/// Null means "not specified" (caller should use defaults).
/// </summary>
public sealed class TenantDashboardModules
{
    public bool? PurchaseApproval { get; init; }
    public bool? SalesApproval { get; init; }
    public bool? ScanPo { get; init; }
    public bool? ReceivedGoods { get; init; }
    /// <summary>Stock-item maintenance scanning (ported from ProAccScanner). Falls back to tenant <c>features.enableScanner</c> when this is null.</summary>
    public bool? MaintenanceScanner { get; init; }
}

/// <summary>
/// Canonical role names used by <see cref="System.Security.Claims.ClaimTypes.Role"/>.
/// </summary>
public static class ApprovalRoles
{
    public const string Admin = "Admin";
    public const string Maintenance = "Maintenance";
}

/// <summary>
/// Per-tenant role allowlists. Emails matched case-insensitively after trim.
/// Users explicitly listed in <c>maintenance</c> are restricted to Maintenance role.
/// Admin-listed users get Admin, and unlisted users default to Maintenance.
/// </summary>
public sealed class TenantUserRoles
{
    public IReadOnlyList<string> Admin { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Maintenance { get; init; } = Array.Empty<string>();

    /// <summary>Returns one effective role for the given email.</summary>
    public IReadOnlyList<string> ResolveRolesFor(string? email)
    {
        var key = (email ?? "").Trim();
        if (key.Length == 0) return new[] { ApprovalRoles.Maintenance };

        var isAdmin = ContainsIgnoreCase(Admin, key);
        var isMaintenance = ContainsIgnoreCase(Maintenance, key);

        if (isAdmin) return new[] { ApprovalRoles.Admin };
        return new[] { ApprovalRoles.Maintenance };
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
