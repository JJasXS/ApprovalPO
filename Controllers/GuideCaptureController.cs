using System.Security.Claims;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalPO.Controllers;

/// <summary>
/// Gated helper for user-guide screenshot capture.
/// Enabled only when APPROVAL_GUIDE_CAPTURE=1.
/// </summary>
[ApiController]
[Route("api/guide_capture")]
public sealed class GuideCaptureController : ControllerBase
{
    private readonly ISyUserLoginValidator _syUser;
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GuideCaptureController> _logger;

    public GuideCaptureController(
        ISyUserLoginValidator syUser,
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        ILogger<GuideCaptureController> logger)
    {
        _syUser = syUser;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
    }

    private static bool IsEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("APPROVAL_GUIDE_CAPTURE"), "1", StringComparison.Ordinal);

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] GuideLoginRequest? body, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
            return NotFound();

        var email = (body?.Email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
            email = (_configuration["Approval:GuideCaptureEmail"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
            email = await TryPickSyUserEmailAsync(cancellationToken).ConfigureAwait(false) ?? "";
        if (string.IsNullOrWhiteSpace(email))
            email = await TryPickAdminEmailAsync(cancellationToken).ConfigureAwait(false) ?? "";

        SyUserEmailLookupResult? sy = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            sy = await _syUser.LookupByEmailAsync(email, cancellationToken).ConfigureAwait(false);
            if (sy.Status != SyUserEmailLookupStatus.Ok && sy.Status != SyUserEmailLookupStatus.Skipped)
            {
                _logger.LogWarning("Guide capture preferred email {Email} lookup {Status}; trying SY_USER fallback.", email, sy.Status);
                email = "";
                sy = null;
            }
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            email = await TryPickSyUserEmailAsync(cancellationToken).ConfigureAwait(false) ?? "";
            if (string.IsNullOrWhiteSpace(email))
                email = await TryPickAdminEmailAsync(cancellationToken).ConfigureAwait(false) ?? "";
            if (!string.IsNullOrWhiteSpace(email))
                sy = await _syUser.LookupByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "No guide email available. Pass {\"email\":\"...\"} or set Approval:GuideCaptureEmail." });

        sy ??= await _syUser.LookupByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (sy.Status != SyUserEmailLookupStatus.Ok && sy.Status != SyUserEmailLookupStatus.Skipped)
        {
            _logger.LogWarning("Guide capture login lookup {Status} for {Email}: {Diag}", sy.Status, email, sy.Diagnostic);
            return BadRequest(new { error = $"User lookup failed: {sy.Status}", diagnostic = sy.Diagnostic });
        }

        var displayName = string.IsNullOrWhiteSpace(sy.DisplayName) ? email : sy.DisplayName!.Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, displayName),
        };
        SyUserApprovalMapper.AddProfileClaims(claims, sy);

        if (!claims.Any(c => c.Type == ClaimTypes.Role
                             && string.Equals(c.Value, ApprovalRoles.Admin, StringComparison.OrdinalIgnoreCase)))
            claims.Add(new Claim(ClaimTypes.Role, ApprovalRoles.Admin));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);

        return Ok(new
        {
            email,
            displayName,
            roles = claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToArray()
        });
    }

    private async Task<string?> TryPickAdminEmailAsync(CancellationToken cancellationToken)
    {
        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            return null;

        try
        {
            var userRoles = await _tenantResolver.GetTenantUserRolesAsync(tenant, cancellationToken).ConfigureAwait(false);
            var admin = userRoles?.Admin?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e) && e.Contains('@'));
            if (!string.IsNullOrWhiteSpace(admin))
                return admin.Trim();

            var maint = userRoles?.Maintenance?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e) && e.Contains('@'));
            if (!string.IsNullOrWhiteSpace(maint))
                return maint.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Guide capture could not load tenant userRoles.");
        }

        return null;
    }

    private async Task<string?> TryPickSyUserEmailAsync(CancellationToken cancellationToken)
    {
        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            return null;

        try
        {
            var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
            await using var conn = new FirebirdSql.Data.FirebirdClient.FbConnection(connStr);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT FIRST 1 EMAIL
FROM SY_USER
WHERE EMAIL IS NOT NULL AND TRIM(EMAIL) <> ''
ORDER BY EMAIL";
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var email = result?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(email) ? null : email;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Guide capture could not query SY_USER for a fallback email.");
            return null;
        }
    }

    public sealed class GuideLoginRequest
    {
        public string? Email { get; set; }
    }
}
