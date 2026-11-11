using ApprovalPO.Services.Databases;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class SelectCompanyModel : PageModel
{
    private readonly IActiveCompanyContext _activeCompany;

    public SelectCompanyModel(IActiveCompanyContext activeCompany)
    {
        _activeCompany = activeCompany;
    }

    public async Task<IActionResult> OnGetAsync(string? id, string? returnUrl, CancellationToken cancellationToken)
    {
        id = (id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id))
            return RedirectToPage("/Dashboard");

        try
        {
            await _activeCompany.SetActiveCompanyAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return RedirectToPage("/Databases");
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return RedirectToPage("/Dashboard");
    }
}
