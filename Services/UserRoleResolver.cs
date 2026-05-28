using ApprovalPO.Helpers;
using ApprovalPO.Models;

namespace ApprovalPO.Services;

/// <summary>
/// Resolves the role names (Admin / Maintenance) that should be attached as
/// <see cref="System.Security.Claims.ClaimTypes.Role"/> claims at login.
/// </summary>
public interface IUserRoleResolver
{
    /// <summary>
    /// Returns role names for the given email.
    /// Unlisted users default to Maintenance; explicit admin users receive Admin.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveRolesForEmailAsync(string email, CancellationToken cancellationToken = default);
}

internal sealed class UserRoleResolver : IUserRoleResolver
{
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserRoleResolver> _logger;

    public UserRoleResolver(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        ILogger<UserRoleResolver> logger)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ResolveRolesForEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            _logger.LogWarning("UserRoleResolver: tenant code missing, defaulting to Maintenance for {Email}.", email);
            return new[] { ApprovalRoles.Maintenance };
        }

        TenantUserRoles? userRoles;
        try
        {
            userRoles = await _tenantResolver.GetTenantUserRolesAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserRoleResolver: tenant role lookup failed for {Email}; defaulting to Maintenance.", email);
            return new[] { ApprovalRoles.Maintenance };
        }

        if (userRoles is null)
            return new[] { ApprovalRoles.Maintenance };

        return userRoles.ResolveRolesFor(email);
    }
}
