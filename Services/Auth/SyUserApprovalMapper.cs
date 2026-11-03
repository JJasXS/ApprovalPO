using System.Globalization;
using System.Security.Claims;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Auth;

/// <summary>
/// Maps <c>SY_USER.UDF_APPROLE</c> to cookie roles and copies amount-range UDFs onto claims.
/// </summary>
public static class SyUserApprovalMapper
{
    /// <summary>Upper amount bound written onto director cookies so client range checks stay open.</summary>
    public const decimal DirectorUnlimitedTo = 999_999_999_999m;

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

        if (sy.IsDirector)
            claims.Add(new Claim(ClaimTypes.Role, ApprovalRoles.Director));

        AddOptional(claims, ApprovalClaimTypes.AppRole, sy.AppRole);
        AddOptional(claims, ApprovalClaimTypes.IsDirector, sy.IsDirector ? "1" : null);

        if (sy.IsDirector)
        {
            AddOptionalDecimal(claims, ApprovalClaimTypes.AppLevel, 3m);
            AddOptionalDecimal(claims, ApprovalClaimTypes.AppFrom, 0m);
            AddOptionalDecimal(claims, ApprovalClaimTypes.AppTo, DirectorUnlimitedTo);
            return;
        }

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

    public static bool IsDirector(ClaimsPrincipal user) =>
        user.IsInRole(ApprovalRoles.Director)
        || string.Equals(user.FindFirst(ApprovalClaimTypes.IsDirector)?.Value, "1", StringComparison.Ordinal);

    public static bool CanApproveFromClaims(ClaimsPrincipal user) =>
        ReadDecimalClaim(user, ApprovalClaimTypes.AppFrom) is not null
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
