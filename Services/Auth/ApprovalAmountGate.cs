using System.Globalization;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Auth;

public sealed class ApprovalAuthorityDecision
{
    public required bool Allowed { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsFinalApproval { get; init; }
    public int CompletedLevels { get; init; }
    public int RequiredLevels { get; init; }
    public string? SuccessMessage { get; init; }

    public static ApprovalAuthorityDecision Allow() => new() { Allowed = true };

    public static ApprovalAuthorityDecision AllowWorkflow(int completed, int required, bool isFinal, string? message) =>
        new()
        {
            Allowed = true,
            CompletedLevels = completed,
            RequiredLevels = required,
            IsFinalApproval = isFinal,
            SuccessMessage = message
        };

    public static ApprovalAuthorityDecision Deny(string message) =>
        new() { Allowed = false, ErrorMessage = message };
}

/// <summary>
/// Inclusive FROM/TO amount gate. Hardcoded 10k/100k bands are not used; those live on SY_USER.
/// </summary>
public static class ApprovalAmountGate
{
    public static ApprovalAuthorityDecision Evaluate(
        string? appRole,
        decimal? from,
        decimal? to,
        decimal amount)
    {
        var roles = SyUserApprovalMapper.MapRoles(appRole);
        var isAdmin = roles.Any(r => string.Equals(r, ApprovalRoles.Admin, StringComparison.OrdinalIgnoreCase));
        if (!isAdmin)
            return ApprovalAuthorityDecision.Deny("You are not authorized to change approval status for this document.");

        if (from is null || to is null)
            return ApprovalAuthorityDecision.Deny("Your approval range is not set. Contact your administrator.");

        if (amount >= from.Value && amount <= to.Value)
            return ApprovalAuthorityDecision.Allow();

        return ApprovalAuthorityDecision.Deny(
            $"This document is outside your approval range (RM {FormatRm(from.Value)} – RM {FormatRm(to.Value)}).");
    }

    /// <summary>Null header amount means the PO/SO row was not found.</summary>
    public static ApprovalAuthorityDecision RequireHeaderAmount(decimal? amount)
    {
        if (amount is null)
            return ApprovalAuthorityDecision.Deny("Document not found.");
        return ApprovalAuthorityDecision.Allow();
    }

    public static string FormatRm(decimal value) =>
        value.ToString("N2", CultureInfo.GetCultureInfo("en-MY"));
}
