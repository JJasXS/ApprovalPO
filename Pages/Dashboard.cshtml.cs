using ApprovalPO.Helpers;
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

    public bool HasAnyModule =>
        ShowPurchaseApproval || ShowSalesApproval || ShowScanPo || ShowReceivedGoods;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Defaults are all true unless tenant config explicitly disables.
        ShowPurchaseApproval = true;
        ShowSalesApproval = true;
        ShowScanPo = true;
        ShowReceivedGoods = true;

        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return;

        var modules = await _tenantResolver
            .GetTenantDashboardModulesAsync(tenantCode, cancellationToken)
            .ConfigureAwait(false);

        if (modules is null)
            return;

        ShowPurchaseApproval = modules.PurchaseApproval ?? true;
        ShowSalesApproval = modules.SalesApproval ?? true;
        ShowScanPo = modules.ScanPo ?? true;
        ShowReceivedGoods = modules.ReceivedGoods ?? true;
    }
}
