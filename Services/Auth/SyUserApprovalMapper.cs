using System.Globalization;
using System.Security.Claims;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Auth;

/// <summary>
/// Maps <c>SY_USER.UDF_APPROLE</c> to cookie roles and copies amount-range UDFs onto claims.
/// </summary>
public static class SyUserApprovalMapper
{
    public static IReadOnlyList<string> MapRoles(string? appRole)
    {
        var key = (appRole ?? "").Trim();
        if (key.Length == 0 || IsMaintenanceLabel(key))
            return new[] { ApprovalRoles.Maintenance };
        return new[] { ApprovalRoles.Admin };
    }

    public static bool IsMaintenanceLabel(string appRole) =>
        appRole.Equals("Maintenance", StringComparison.OrdinalIgnoreCase)
        || appRole.Equals("Maint", StringComparison.OrdinalIgnoreCase)
        || appRole.Equals("Scanner", StringComparison.OrdinalIgnoreCase);

    public static void AddProfileClaims(List<Claim> claims, SyUserEmailLookupResult sy)
    {
        foreach (var role in MapRoles(sy.AppRole))
        {
            if (!string.IsNullOrWhiteSpace(role))
                claims.Add(new Claim(ClaimTypes.Role, role));
        }

        AddOptional(claims, ApprovalClaimTypes.AppRole, sy.AppRole);
        AddOptionalDecimal(claims, ApprovalClaimTypes.AppLevel, sy.AppLevel);
        AddOptionalDecimal(claims, ApprovalClaimTypes.AppFrom, sy.AppFrom);
        AddOptionalDecimal(claims, ApprovalClaimTypes.AppTo, sy.AppTo);
    }

    public static decimal? ReadDecimalClaim(ClaimsPrincipal user, string claimType)
    {
        var raw = user.FindFirst(claimType)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    public static bool CanApproveFromClaims(ClaimsPrincipal user) =>
        user.IsInRole(ApprovalRoles.Admin)
        && ReadDecimalClaim(user, ApprovalClaimTypes.AppFrom) is not null
        && ReadDecimalClaim(user, ApprovalClaimTypes.AppTo) is not null;

    private static void AddOptional(List<Claim> claims, string type, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0)
            return;
        claims.Add(new Claim(type, v));
    }

    private static void AddOptionalDecimal(List<Claim> claims, string type, decimal? value)
    {
        if (value is null)
            return;
        claims.Add(new Claim(type, value.Value.ToString(CultureInfo.InvariantCulture)));
    }
}
