using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class DashboardModel : PageModel
{
    private readonly IModuleAccessService _modules;

    public DashboardModel(IModuleAccessService modules)
    {
        _modules = modules;
    }

    public bool ShowPurchaseApproval { get; private set; } = true;
    public bool ShowSalesApproval { get; private set; } = true;
    public bool ShowScanPo { get; private set; } = true;
    public bool ShowReceivedGoods { get; private set; } = true;
    public bool ShowMaintenanceScanner { get; private set; } = true;

    public bool HasAnyModule =>
        ShowPurchaseApproval || ShowSalesApproval || ShowScanPo || ShowReceivedGoods || ShowMaintenanceScanner;

    public string RoleLabel { get; private set; } = "";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var flags = await _modules.GetDashboardModulesAsync(User, cancellationToken).ConfigureAwait(false);
        ShowPurchaseApproval = flags.PurchaseApproval;
        ShowSalesApproval = flags.SalesApproval;
        ShowScanPo = flags.ScanPo;
        ShowReceivedGoods = flags.ReceivedGoods;
        ShowMaintenanceScanner = flags.MaintenanceScanner;

        if (User.IsInRole(ApprovalRoles.Admin) && User.IsInRole(ApprovalRoles.Maintenance))
            RoleLabel = "Admin · Maintenance";
        else if (User.IsInRole(ApprovalRoles.Admin))
            RoleLabel = "Admin";
        else if (User.IsInRole(ApprovalRoles.Maintenance))
            RoleLabel = "Maintenance";
    }
}
