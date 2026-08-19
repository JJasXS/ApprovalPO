using System.Security.Claims;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Auth;

public interface IApprovalAuthority
{
    Task<ApprovalAuthorityDecision> AuthorizeListStatusChangeAsync(
        ClaimsPrincipal user,
        decimal amount,
        CancellationToken cancellationToken = default);

    Task<ApprovalAuthorityDecision> AuthorizePartialApproveAsync(
        ClaimsPrincipal user,
        decimal amount,
        PoApprovalSlots currentSlots,
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
        var ctx = await ResolveUserContextAsync(user, cancellationToken).ConfigureAwait(false);
        if (ctx.Deny is not null)
            return ctx.Deny;

        return ApprovalAmountGate.Evaluate(ctx.AppRole, ctx.AppFrom, ctx.AppTo, amount);
    }

    public async Task<ApprovalAuthorityDecision> AuthorizePartialApproveAsync(
        ClaimsPrincipal user,
        decimal amount,
        PoApprovalSlots currentSlots,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveUserContextAsync(user, cancellationToken).ConfigureAwait(false);
        if (ctx.Deny is not null)
            return ctx.Deny;

        // Amount must be within the user's configured approval range.
        var amountDecision = ApprovalAmountGate.Evaluate(ctx.AppRole, ctx.AppFrom, ctx.AppTo, amount);
        if (!amountDecision.Allowed)
            return amountDecision;

        var userLevel = ApprovalLevelWorkflow.ResolveUserLevel(ctx.AppLevel, ctx.AppRole);
        if (userLevel <= 0)
            return ApprovalAuthorityDecision.Deny("Your approval level is not set. Contact your administrator.");

        // Prevent the same person approving twice at the same slot level.
        var existingAtLevel = currentSlots.EmailAtLevel(userLevel);
        if (ApprovalLevelWorkflow.EmailMatches(existingAtLevel, ctx.Email))
            return ApprovalAuthorityDecision.Deny("You have already approved at this level.");

        // Higher-level approvers can approve without waiting for lower levels —
        // record sign-off at the user's own level slot and treat it as final.
        var required = ApprovalLevelWorkflow.RequiredLevels(amount);
        var completed = ApprovalLevelWorkflow.CompletedLevels(currentSlots);
        var newCompleted = completed + 1;
        var isFinal = true; // any authorised approver makes it final
        var message = $"Approved by {ApprovalLevelWorkflow.LevelLabel(userLevel)}.";
        return ApprovalAuthorityDecision.AllowWorkflow(newCompleted, required, isFinal, message);
    }

    private async Task<UserApprovalContext> ResolveUserContextAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var email = user.FindFirstValue(ClaimTypes.Email)
                    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? "";
        if (string.IsNullOrWhiteSpace(email))
            return UserApprovalContext.Denied("You are not signed in.");

        var sy = await _syUser.LookupByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (sy.Status == SyUserEmailLookupStatus.Skipped)
            return UserApprovalContext.FromSkipped(email, sy);

        if (sy.Status != SyUserEmailLookupStatus.Ok)
            return UserApprovalContext.Denied("Could not verify your approval authority. Try again later.");

        return UserApprovalContext.FromOk(email, sy);
    }

    private sealed record UserApprovalContext(
        string Email,
        string? AppRole,
        decimal? AppLevel,
        decimal? AppFrom,
        decimal? AppTo,
        ApprovalAuthorityDecision? Deny)
    {
        public static UserApprovalContext Denied(string message) =>
            new("", null, null, null, null, ApprovalAuthorityDecision.Deny(message));

        public static UserApprovalContext FromSkipped(string email, SyUserEmailLookupResult sy) =>
            new(email, sy.AppRole, sy.AppLevel, sy.AppFrom, sy.AppTo, null);

        public static UserApprovalContext FromOk(string email, SyUserEmailLookupResult sy) =>
            new(email, sy.AppRole, sy.AppLevel, sy.AppFrom, sy.AppTo, null);
    }
}
