using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    private static readonly Regex SixDigitOtp = new(@"^\d{6}$", RegexOptions.Compiled);

    [BindProperty]
    public string LoginId { get; set; } = "";

    [BindProperty]
    public string Otp { get; set; } = "";

    public void OnGet()
    {
    }

    /// <summary>Mock: no email is sent; always succeeds when login id is non-empty.</summary>
    public IActionResult OnPostSendOtp()
    {
        LoginId = (LoginId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(LoginId))
            return new JsonResult(new { success = false, message = "Please enter your email or username." });

        return new JsonResult(new { success = true, message = "OTP sent (mock). Enter any 6-digit code." });
    }

    public async Task<IActionResult> OnPostVerifyOtpAsync()
    {
        LoginId = (LoginId ?? "").Trim();
        Otp = (Otp ?? "").Trim();

        if (string.IsNullOrWhiteSpace(LoginId))
            return new JsonResult(new { success = false, message = "Email or username is missing. Go back and try again." });

        if (!SixDigitOtp.IsMatch(Otp))
            return new JsonResult(new { success = false, message = "OTP must be exactly 6 digits." });

        var displayName = LoginId.Contains('@', StringComparison.Ordinal) ? LoginId : LoginId;
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, LoginId),
            new Claim(ClaimTypes.Name, displayName),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        return new JsonResult(new
        {
            success = true,
            message = "Signed in.",
            redirectUrl = Url.Page("/PurchaseOrders") ?? "/PurchaseOrders"
        });
    }
}
