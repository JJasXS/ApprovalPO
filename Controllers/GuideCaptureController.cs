using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ApprovalPO.Authorization;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalPO.Controllers;

/// <summary>
/// Gated helper for user-guide screenshot capture.
/// Enabled only when APPROVAL_GUIDE_CAPTURE=1 and a shared secret is configured.
/// Does not elevate roles beyond SY_USER claims.
/// </summary>
[ApiController]
[Route("api/guide_capture")]
[Authorize(Policy = PolicyNames.DirectorAccess)]
public sealed class GuideCaptureController : ControllerBase
{
    private readonly ISyUserLoginValidator _syUser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GuideCaptureController> _logger;

    public GuideCaptureController(
        ISyUserLoginValidator syUser,
        IConfiguration configuration,
        ILogger<GuideCaptureController> logger)
    {
        _syUser = syUser;
        _configuration = configuration;
        _logger = logger;
    }

    private static bool IsEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("APPROVAL_GUIDE_CAPTURE"), "1", StringComparison.Ordinal);

    private bool SharedSecretOk()
    {
        var expected = (_configuration["Approval:GuideCaptureSharedSecret"] ?? "").Trim();
        if (expected.Length < 16)
            return false;

        var provided = (Request.Headers["X-Guide-Capture-Secret"].ToString() ?? "").Trim();
        if (provided.Length == 0)
            provided = (Request.Query["secret"].ToString() ?? "").Trim();
        if (provided.Length == 0)
            return false;

        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] GuideLoginRequest? body, CancellationToken cancellationToken)
    {
        if (!IsEnabled() || !SharedSecretOk())
            return NotFound();

        var email = (body?.Email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
            email = (_configuration["Approval:GuideCaptureEmail"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "Pass {\"email\":\"...\"} or set Approval:GuideCaptureEmail." });

        var sy = await _syUser.LookupByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (sy.Status != SyUserEmailLookupStatus.Ok && sy.Status != SyUserEmailLookupStatus.Skipped)
        {
            _logger.LogWarning("Guide capture login lookup {Status} for {Email}", sy.Status, email);
            return BadRequest(new { error = $"User lookup failed: {sy.Status}" });
        }

        if (!SyUserApprovalMapper.CanAccessApprovalSystem(sy))
            return Forbid();

        var displayName = string.IsNullOrWhiteSpace(sy.DisplayName) ? email : sy.DisplayName!.Trim();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, displayName),
        };
        // Roles come only from SY_USER — no Admin/Director elevation.
        SyUserApprovalMapper.AddProfileClaims(claims, sy);

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

    public sealed class GuideLoginRequest
    {
        public string? Email { get; set; }
    }
}
