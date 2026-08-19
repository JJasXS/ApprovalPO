using System.Security.Claims;

namespace ApprovalPO.Services.Auth;

public interface IApprovalAuthority
{
    Task<ApprovalAuthorityDecision> AuthorizeListStatusChangeAsync(
        ClaimsPrincipal user,
        decimal amount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Re-reads SY_USER on each status change so UDF edits apply without re-login.
/// </summary>
public sealed class ApprovalAuthority : IApprovalAuthority
{
    private readonly ISyUserLoginValidator _syUser;

    public ApprovalAuthority(ISyUserLoginValidator syUser)
    {
        _syUser = syUser;
    }

    public async Task<ApprovalAuthorityDecision> AuthorizeListStatusChangeAsync(
        ClaimsPrincipal user,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        var email = user.FindFirstValue(ClaimTypes.Email)
                    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? "";
        if (string.IsNullOrWhiteSpace(email))
            return ApprovalAuthorityDecision.Deny("You are not signed in.");

        var sy = await _syUser.LookupByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (sy.Status == SyUserEmailLookupStatus.Skipped)
            return ApprovalAmountGate.Evaluate(sy.AppRole, sy.AppFrom, sy.AppTo, amount);

        if (sy.Status != SyUserEmailLookupStatus.Ok)
            return ApprovalAuthorityDecision.Deny("Could not verify your approval authority. Try again later.");

        return ApprovalAmountGate.Evaluate(sy.AppRole, sy.AppFrom, sy.AppTo, amount);
    }
}
