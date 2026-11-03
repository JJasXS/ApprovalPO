using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class NoAccessModel : PageModel
{
    private readonly IModuleAccessService _modules;

    public NoAccessModel(IModuleAccessService modules)
    {
        _modules = modules;
    }

    public bool ShowMaintenanceScanner { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!CanUseMaintenanceScannerRole())
        {
            ShowMaintenanceScanner = false;
            return;
        }

        var flags = await _modules.GetDashboardModulesAsync(User, cancellationToken).ConfigureAwait(false);
        ShowMaintenanceScanner = flags.MaintenanceScanner;
    }

    private bool CanUseMaintenanceScannerRole() =>
        User.IsInRole(ApprovalRoles.Admin)
        || User.IsInRole(ApprovalRoles.Maintenance)
        || User.IsInRole(ApprovalRoles.Director);
}
