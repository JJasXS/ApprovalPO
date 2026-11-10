using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
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

        if (!SyUserApprovalMapper.CanAccessApprovalSystem(User))
            return RedirectToPage("/NoAccess");

        return RedirectToPage("/Dashboard");
    }
}
