using ApprovalPO.Models;

namespace ApprovalPO.Services.Auth;

/// <summary>
/// Resolves Admin / Maintenance from <c>SY_USER.UDF_APPROLE</c> (not Dynamo email lists).
/// </summary>
public interface IUserRoleResolver
{
    Task<IReadOnlyList<string>> ResolveRolesForEmailAsync(string email, CancellationToken cancellationToken = default);
}

internal sealed class UserRoleResolver : IUserRoleResolver
{
    private readonly ISyUserLoginValidator _syUser;
    private readonly ILogger<UserRoleResolver> _logger;

    public UserRoleResolver(ISyUserLoginValidator syUser, ILogger<UserRoleResolver> logger)
    {
        _syUser = syUser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ResolveRolesForEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var sy = await _syUser.LookupByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (sy.Status != SyUserEmailLookupStatus.Ok && sy.Status != SyUserEmailLookupStatus.Skipped)
        {
            _logger.LogWarning(
                "UserRoleResolver: SY_USER lookup {Status} for {Email}; defaulting to Maintenance.",
                sy.Status,
                email);
            return new[] { ApprovalRoles.Maintenance };
        }

        return SyUserApprovalMapper.MapRoles(sy.AppRole);
    }
}
