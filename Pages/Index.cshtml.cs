using ApprovalPO.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[AllowAnonymous]
public class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated != true)
            return RedirectToPage("/Login");

        if (!User.IsInRole(ApprovalRoles.Director))
            return RedirectToPage("/NoAccess");

        return RedirectToPage("/Dashboard");
    }
}
