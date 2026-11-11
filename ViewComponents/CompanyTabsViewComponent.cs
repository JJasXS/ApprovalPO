using ApprovalPO.Services.Databases;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalPO.ViewComponents;

public sealed class CompanyTabsViewComponent : ViewComponent
{
    private readonly IActiveCompanyContext _activeCompany;

    public CompanyTabsViewComponent(IActiveCompanyContext activeCompany)
    {
        _activeCompany = activeCompany;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var companies = await _activeCompany.ListCompaniesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var explicitCompanies = companies.Where(c => !c.UseTenantBootstrap).ToList();
        // Hide when only one (or zero) usable company — same menus, no need for a strip.
        if (explicitCompanies.Count < 2 && companies.Count < 2)
            return Content(string.Empty);

        var list = explicitCompanies.Count >= 2 ? explicitCompanies : companies.ToList();
        var active = await _activeCompany.GetActiveCompanyAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var returnUrl = (HttpContext.Request.Path + HttpContext.Request.QueryString).ToString();
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl == "/")
            returnUrl = "/Dashboard";

        return View(new CompanyTabsModel
        {
            Companies = list
                .Select(c => new CompanyTabItem
                {
                    Id = c.Id,
                    DisplayName = c.DisplayName,
                    IsActive = string.Equals(c.Id, active?.Id, StringComparison.OrdinalIgnoreCase),
                })
                .ToList(),
            ReturnUrl = returnUrl,
        });
    }
}

public sealed class CompanyTabsModel
{
    public IReadOnlyList<CompanyTabItem> Companies { get; init; } = Array.Empty<CompanyTabItem>();
    public string ReturnUrl { get; init; } = "/Dashboard";
}

public sealed class CompanyTabItem
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsActive { get; init; }
}
