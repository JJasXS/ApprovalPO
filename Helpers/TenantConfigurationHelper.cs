namespace ApprovalPO.Helpers;

public static class TenantConfigurationHelper
{
    /// <summary>Reads tenant code when configured; empty string otherwise.</summary>
    public static string GetTenantCodeOrEmpty(IConfiguration configuration) =>
        RequireTenantCodeOrNull(configuration) ?? string.Empty;

    /// <summary>Reads <c>TenantBootstrap:TenantCode</c> or <c>TENANT_CODE</c>; null when missing.</summary>
    public static string? RequireTenantCodeOrNull(IConfiguration configuration)
    {
        var tenant = (configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            tenant = (configuration["TENANT_CODE"] ?? "").Trim();
        return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
    }

    /// <summary>Soft tenant lookup for APIs that return error tuples instead of throwing.</summary>
    public static bool TryGetTenantCode(IConfiguration configuration, out string tenant, out string? error, string? operation = null)
    {
        tenant = RequireTenantCodeOrNull(configuration) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            error = null;
            return true;
        }

        error = string.IsNullOrWhiteSpace(operation)
            ? "TenantBootstrap:TenantCode is required."
            : $"TenantBootstrap:TenantCode is required to {operation}.";
        return false;
    }

    /// <summary>Reads <c>TenantBootstrap:TenantCode</c> or <c>TENANT_CODE</c>; throws when missing.</summary>
    public static string RequireTenantCode(IConfiguration configuration, string? operation = null)
    {
        var tenant = RequireTenantCodeOrNull(configuration);
        if (!string.IsNullOrWhiteSpace(tenant))
            return tenant;

        var suffix = string.IsNullOrWhiteSpace(operation) ? "." : $" to {operation}.";
        throw new InvalidOperationException($"TenantBootstrap:TenantCode is required{suffix}");
    }
}
