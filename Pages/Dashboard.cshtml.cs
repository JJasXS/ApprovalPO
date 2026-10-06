using ApprovalPO.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class DashboardModel : PageModel
{
    private readonly DashboardOverviewService _overview;

    public DashboardModel(DashboardOverviewService overview)
    {
        _overview = overview;
    }

    public bool ShowPurchaseApproval { get; private set; } = true;
    public bool ShowSalesApproval { get; private set; } = true;
    public bool ShowScanPo { get; private set; } = true;
    public bool ShowReceivedGoods { get; private set; } = true;
    public bool ShowMaintenanceScanner { get; private set; } = true;
    public IReadOnlyList<DashboardFigure> Figures { get; private set; } = Array.Empty<DashboardFigure>();

    public bool HasAnyModule =>
        ShowPurchaseApproval || ShowSalesApproval || ShowScanPo || ShowReceivedGoods || ShowMaintenanceScanner;

    public string GoodsHref => ShowScanPo ? "/ScanPO" : "/ReceivedGoods";

    public DashboardFigure? Figure(string key) =>
        Figures.FirstOrDefault(f => f.Key == key);

    public static string CardLine(DashboardFigure? fig, string unit)
    {
        if (fig?.Count is not int count || fig.Amount is not decimal amount)
            return "Totals unavailable";
        return $"{count:N0} {unit} · {amount:N2}";
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var overview = await _overview.LoadAsync(User, cancellationToken).ConfigureAwait(false);
        var modules = overview.Modules;
        ShowPurchaseApproval = modules.PurchaseApproval;
        ShowSalesApproval = modules.SalesApproval;
        ShowScanPo = modules.ScanPo;
        ShowReceivedGoods = modules.ReceivedGoods;
        ShowMaintenanceScanner = modules.MaintenanceScanner;
        Figures = overview.Figures;
    }
}
