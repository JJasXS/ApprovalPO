using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApprovalPO.Options;
using ApprovalPO.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Pages;

[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    private static readonly Regex SixDigitOtp = new(@"^\d{6}$", RegexOptions.Compiled);

    private readonly OtpSessionStore _otpStore;
    private readonly IOtpEmailSender _emailSender;
    private readonly ApprovalOptions _approval;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        OtpSessionStore otpStore,
        IOtpEmailSender emailSender,
        IOptions<ApprovalOptions> approval,
        IWebHostEnvironment env,
        ILogger<LoginModel> logger)
    {
        _otpStore = otpStore;
        _emailSender = emailSender;
        _approval = approval.Value;
        _env = env;
        _logger = logger;
    }

    [BindProperty]
    public string LoginId { get; set; } = "";

    [BindProperty]
    public string Otp { get; set; } = "";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostSendOtpAsync(CancellationToken cancellationToken)
    {
        LoginId = (LoginId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(LoginId))
            return new JsonResult(new { success = false, message = "Please enter your email or username." });

        if (!LoginId.Contains('@', StringComparison.Ordinal))
            return new JsonResult(new { success = false, message = "Enter an email address so we can send your OTP." });

        var (ok, message, otpPlain) = _otpStore.TryBeginSend(LoginId);
        if (!ok || string.IsNullOrEmpty(otpPlain))
            return new JsonResult(new { success = false, message });

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

    public async Task<IActionResult> OnPostVerifyOtpAsync()
    {
        LoginId = (LoginId ?? "").Trim();
        Otp = (Otp ?? "").Trim();

        if (string.IsNullOrWhiteSpace(LoginId))
            return new JsonResult(new { success = false, message = "Email is missing." });

        if (!SixDigitOtp.IsMatch(Otp))
            return new JsonResult(new { success = false, message = "OTP must be exactly 6 digits." });

        var (ok, message) = _otpStore.TryVerify(LoginId, Otp);
        if (!ok)
            return new JsonResult(new { success = false, message });

        var displayName = LoginId;
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

        return new JsonResult(new
        {
            success = true,
            message = "Signed in.",
            redirectUrl = Url.Page("/PurchaseOrders") ?? "/PurchaseOrders"
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
