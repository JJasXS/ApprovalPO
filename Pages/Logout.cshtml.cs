using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    /// <summary>GET does not sign out (prevents logout CSRF via links).</summary>
    public IActionResult OnGet() => RedirectToPage("/Dashboard");

    public async Task<IActionResult> OnPostAsync() => await SignOutAndRedirectAsync().ConfigureAwait(false);

    private async Task<IActionResult> SignOutAndRedirectAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        return RedirectToPage("/Login");
    }
}
