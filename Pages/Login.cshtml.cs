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
    private readonly LoginIpThrottle _ipThrottle;
    private readonly IOtpEmailSender _emailSender;
    private readonly ISyUserLoginValidator _syUser;
    private readonly ApprovalOptions _approval;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        OtpSessionStore otpStore,
        LoginIpThrottle ipThrottle,
        IOtpEmailSender emailSender,
        ISyUserLoginValidator syUser,
        IOptions<ApprovalOptions> approval,
        IWebHostEnvironment env,
        ILogger<LoginModel> logger)
    {
        _otpStore = otpStore;
        _ipThrottle = ipThrottle;
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

    /// <summary>Honeypot field; must stay empty (bots often fill hidden &quot;website&quot; fields).</summary>
    [BindProperty(Name = "companyWebsite")]
    public string? CompanyWebsite { get; set; }

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
        if (HoneypotTripped())
            return HoneypotJson();

        if (!_ipThrottle.TrySend(HttpContext, out var sendLimitMsg))
            return JsonThrottle(sendLimitMsg);

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
            var allowWithoutEmail = AllowOtpWithoutEmailDelivery();
            if (!allowWithoutEmail)
            {
                _logger.LogWarning("OTP email not sent for {LoginId}: {Error}", LoginId, err);
                return new JsonResult(new { success = false, message = err ?? "Could not send email. Try again later." });
            }

            _logger.LogInformation(
                "OTP email not sent for {LoginId} (login continues; fix SMTP when ready): {Error}",
                LoginId,
                err);
        }

        var payload = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = sent
                ? "OTP sent to your email."
                : "OTP generated (email not delivered — use the code shown below)."
        };

        if (_env.IsDevelopment())
        {
            payload["devOtp"] = otpPlain;
            payload["message"] = (sent ? "OTP sent. " : "") + $"Development: OTP is {otpPlain}";
        }
        else if (!sent && !_env.IsDevelopment())
        {
            payload["devOtp"] = otpPlain;
            payload["message"] =
                "SMTP failed; emergency bypass is on. Your OTP is " + otpPlain + ". Turn off Approval:ExposeOtpWhenEmailFails or APPROVALPO_ALLOW_OTP_WITHOUT_EMAIL after fixing mail.";
        }

        return new JsonResult(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>Development always; Production when appsettings or flat env says to continue without SMTP.</summary>
    private bool AllowOtpWithoutEmailDelivery()
    {
        if (_env.IsDevelopment())
            return true;
        if (_approval.ExposeOtpWhenEmailFails)
            return true;
        var v = (Environment.GetEnvironmentVariable("APPROVALPO_ALLOW_OTP_WITHOUT_EMAIL") ?? "").Trim();
        return v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v == "1"
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IActionResult> OnPostVerifyOtpAsync(CancellationToken cancellationToken)
    {
        if (HoneypotTripped())
            return HoneypotJson();

        if (!_ipThrottle.TryVerify(HttpContext, out var verifyLimitMsg))
            return JsonThrottle(verifyLimitMsg);

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

        if (IsLikelyPurchaseOrdersPage(redirect))
            redirect = AppendQueryParam(redirect, "promptNotify", "1");

        return new JsonResult(new
        {
            success = true,
            message = "Signed in.",
            redirectUrl = redirect
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private bool HoneypotTripped()
    {
        if (string.IsNullOrWhiteSpace(CompanyWebsite))
            return false;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogWarning("Login honeypot field filled from {Ip}", ip);
        return true;
    }

    private IActionResult HoneypotJson() =>
        new JsonResult(
            new { success = false, message = "Sign-in could not be completed. Please try again." },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private IActionResult JsonThrottle(string message)
    {
        Response.Headers.Append("Retry-After", "60");
        return new JsonResult(
            new { success = false, message },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
        };
    }

    private static bool IsLikelyPurchaseOrdersPage(string pathAndQuery)
    {
        var p = (pathAndQuery ?? "").Trim();
        if (p.Length == 0)
            return false;
        var q = p.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
            p = p[..q];
        return p.Equals("/PurchaseOrders", StringComparison.OrdinalIgnoreCase)
               || p.Equals("PurchaseOrders", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendQueryParam(string pathAndQuery, string key, string value)
    {
        var sep = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{pathAndQuery}{sep}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
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
                "Login cannot verify your email: tenant is not configured. Set TENANT_CODE in the ApprovalPO folder .env (or TenantBootstrap:TenantCode in appsettings) and ensure TenantBootstrap:AwsApiBaseUrl and Firebird user/password are set.",
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
