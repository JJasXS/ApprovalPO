using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApprovalPO.Options;
using ApprovalPO.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    private static readonly Regex SixDigitOtp = new(@"^\d{6}$", RegexOptions.Compiled);

    private readonly OtpSessionStore _otpStore;
    private readonly IOtpEmailSender _emailSender;
    private readonly ISyUserLoginValidator _syUser;
    private readonly ApprovalOptions _approval;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        OtpSessionStore otpStore,
        IOtpEmailSender emailSender,
        ISyUserLoginValidator syUser,
        IOptions<ApprovalOptions> approval,
        IWebHostEnvironment env,
        ILogger<LoginModel> logger)
    {
        _otpStore = otpStore;
        _emailSender = emailSender;
        _syUser = syUser;
        _approval = approval.Value;
        _env = env;
        _logger = logger;
    }

    [BindProperty]
    public string LoginId { get; set; } = "";

    [BindProperty]
    public string Otp { get; set; } = "";

    /// <summary>After sign-in redirect; only same-site relative paths are accepted.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/PurchaseOrders");

        ReturnUrl = NormalizeLocalReturn(ReturnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostSendOtpAsync(CancellationToken cancellationToken)
    {
        LoginId = (LoginId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(LoginId))
            return new JsonResult(new { success = false, message = "Please enter your email or username." });

        if (!LoginId.Contains('@', StringComparison.Ordinal))
            return new JsonResult(new { success = false, message = "Enter an email address so we can send your OTP." });

        var sy = await _syUser.LookupByEmailAsync(LoginId, cancellationToken).ConfigureAwait(false);
        if (MapSyUserBlockMessage(sy.Status) != null)
            return JsonSyUserFailure(sy);

        var (ok, message, otpPlain) = _otpStore.TryBeginSend(LoginId);
        if (!ok || string.IsNullOrEmpty(otpPlain))
            return new JsonResult(new { success = false, message });

        if (_approval.DebugOtpToConsole)
        {
            var line = $"[DEBUG] OTP for {LoginId}: {otpPlain}";
            Console.WriteLine(line);
            _logger.LogInformation("{OtpDebugLine}", line);
        }

        var (sent, err) = await _emailSender.SendOtpAsync(LoginId, otpPlain, cancellationToken).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogWarning("OTP email not sent for {LoginId}: {Error}", LoginId, err);
            if (!_env.IsDevelopment())
                return new JsonResult(new { success = false, message = err ?? "Could not send email. Try again later." });
        }

        var payload = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = sent
                ? "OTP sent to your email."
                : "OTP generated (email not configured — check with administrator)."
        };

        if (_env.IsDevelopment())
        {
            payload["devOtp"] = otpPlain;
            payload["message"] = (sent ? "OTP sent. " : "") + $"Development: OTP is {otpPlain}";
        }

        return new JsonResult(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public async Task<IActionResult> OnPostVerifyOtpAsync(CancellationToken cancellationToken)
    {
        LoginId = (LoginId ?? "").Trim();
        Otp = (Otp ?? "").Trim();

        if (string.IsNullOrWhiteSpace(LoginId))
            return new JsonResult(new { success = false, message = "Email is missing." });

        if (!SixDigitOtp.IsMatch(Otp))
            return new JsonResult(new { success = false, message = "OTP must be exactly 6 digits." });

        var sy = await _syUser.LookupByEmailAsync(LoginId, cancellationToken).ConfigureAwait(false);
        if (MapSyUserBlockMessage(sy.Status) != null)
            return JsonSyUserFailure(sy);

        var (ok, message) = _otpStore.TryVerify(LoginId, Otp);
        if (!ok)
            return new JsonResult(new { success = false, message });

        ReturnUrl = NormalizeLocalReturn(ReturnUrl);

        var displayName = string.IsNullOrWhiteSpace(sy.DisplayName) ? LoginId : sy.DisplayName.Trim();
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, LoginId),
            new Claim(ClaimTypes.Email, LoginId),
            new Claim(ClaimTypes.Name, displayName),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);

        var redirect = !string.IsNullOrEmpty(ReturnUrl)
            ? ReturnUrl!
            : Url.Page("/PurchaseOrders") ?? "/PurchaseOrders";

        return new JsonResult(new
        {
            success = true,
            message = "Signed in.",
            redirectUrl = redirect
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private string? NormalizeLocalReturn(string? url)
    {
        var u = (url ?? "").Trim();
        if (string.IsNullOrEmpty(u))
            return null;
        if (!Url.IsLocalUrl(u))
            return null;
        return u;
    }

    private string? MapSyUserBlockMessage(SyUserEmailLookupStatus status) =>
        status switch
        {
            SyUserEmailLookupStatus.Ok => null,
            SyUserEmailLookupStatus.Skipped => null,
            SyUserEmailLookupStatus.NotFound =>
                "This email is not registered. Use the same email as your SQL Accounting user (SY_USER).",
            SyUserEmailLookupStatus.Inactive =>
                "This user is inactive in SQL Accounting. Contact your administrator.",
            SyUserEmailLookupStatus.ConfigurationMissing =>
                "Login cannot verify your email: tenant database settings are missing. Configure TenantBootstrap in appsettings or .env (or set Approval:SkipSyUserEmailCheck for development only).",
            SyUserEmailLookupStatus.DatabaseUnavailable =>
                "We could not reach the company database to verify your email. Try again later.",
            _ => "Your email could not be verified.",
        };

    private JsonResult JsonSyUserFailure(SyUserEmailLookupResult sy)
    {
        var message = MapSyUserBlockMessage(sy.Status) ?? "Your email could not be verified.";
        var payload = new Dictionary<string, object?> { ["success"] = false, ["message"] = message };
        if (_env.IsDevelopment() && !string.IsNullOrWhiteSpace(sy.Diagnostic))
        {
            var d = sy.Diagnostic!;
            if (d.Length > 2000)
                d = d[..2000] + "…";
            payload["diagnostic"] = d;
        }

        return new JsonResult(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
