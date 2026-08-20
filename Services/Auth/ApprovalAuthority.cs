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

        var userLevel = ApprovalLevelWorkflow.ResolveUserLevel(ctx.AppLevel, ctx.AppRole);
        if (userLevel <= 0)
            return ApprovalAuthorityDecision.Deny("Your approval level is not set. Contact your administrator.");

        // Prevent the same person approving twice at their own level slot.
        var existingAtLevel = currentSlots.EmailAtLevel(userLevel);
        if (ApprovalLevelWorkflow.EmailMatches(existingAtLevel, ctx.Email))
            return ApprovalAuthorityDecision.Deny("You have already approved at this level.");

        var required = ApprovalLevelWorkflow.RequiredLevels(amount);
        if (required <= 0)
        {
            return ApprovalAuthorityDecision.Deny(
                $"This document amount exceeds the maximum approval limit (RM {ApprovalAmountGate.FormatRm(ApprovalLevelWorkflow.BandLevel3Max)}).");
        }

        var newCompleted = ApprovalLevelWorkflow.CompletedLevelsAfterSign(currentSlots, userLevel);
        var isFinal = newCompleted >= required;

        // Final sign-off uses personal appTo; partial sign-off uses the workflow band ceiling
        // so lower levels can record progress (Waiting tab) even above personal appTo.
        var effectiveTo = isFinal
            ? ctx.AppTo
            : ApprovalLevelWorkflow.BandMaxForRequiredLevels(required);

        var amountDecision = ApprovalAmountGate.Evaluate(ctx.AppRole, ctx.AppFrom, effectiveTo, amount);
        if (!amountDecision.Allowed)
            return amountDecision;

        var message = ApprovalLevelWorkflow.BuildProgressMessage(newCompleted, required, 0, isFinal);
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
