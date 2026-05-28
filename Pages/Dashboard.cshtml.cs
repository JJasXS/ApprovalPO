using ApprovalPO.Helpers;
using ApprovalPO.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class DashboardModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly TenantDbConnectionResolver _tenantResolver;

    public DashboardModel(IConfiguration configuration, TenantDbConnectionResolver tenantResolver)
    {
        _configuration = configuration;
        _tenantResolver = tenantResolver;
    }

    public bool ShowPurchaseApproval { get; private set; } = true;
    public bool ShowSalesApproval { get; private set; } = true;
    public bool ShowScanPo { get; private set; } = true;
    public bool ShowReceivedGoods { get; private set; } = true;
    public bool ShowMaintenanceScanner { get; private set; } = true;

    public bool HasAnyModule =>
        ShowPurchaseApproval || ShowSalesApproval || ShowScanPo || ShowReceivedGoods || ShowMaintenanceScanner;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ShowPurchaseApproval = true;
        ShowSalesApproval = true;
        ShowScanPo = true;
        ShowReceivedGoods = true;
        ShowMaintenanceScanner = true;

        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(tenantCode))
        {
            var isMaintenance = User.IsInRole(ApprovalRoles.Maintenance);
            var modules = await _tenantResolver
                .GetTenantDashboardModulesForRoleAsync(tenantCode, isMaintenance, cancellationToken)
                .ConfigureAwait(false);

            if (modules is not null)
            {
                ShowPurchaseApproval = modules.PurchaseApproval ?? true;
                ShowSalesApproval = modules.SalesApproval ?? true;
                ShowScanPo = modules.ScanPo ?? true;
                ShowReceivedGoods = modules.ReceivedGoods ?? true;
                ShowMaintenanceScanner = modules.MaintenanceScanner ?? true;
            }
        }

        // Dashboard visibility now follows tenant role-specific module flags directly
        // (adminDashboardModules / maintenanceDashboardModules, with fallback).
    }
}
