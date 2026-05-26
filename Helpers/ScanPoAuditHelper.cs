using System.Security.Claims;
using ApprovalPO.Models;

namespace ApprovalPO.Helpers;

public static class ScanPoAuditHelper
{
    public static ScanPoAuditActor FromUser(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return new ScanPoAuditActor();

        var email = (user.FindFirstValue(ClaimTypes.Email)
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "").Trim();

        var displayName = (user.Identity?.Name
            ?? user.FindFirstValue(ClaimTypes.Name)
            ?? email).Trim();

        if (string.IsNullOrEmpty(displayName))
            displayName = email;

        return new ScanPoAuditActor
        {
            Email = email,
            DisplayName = displayName
        };
    }
}
