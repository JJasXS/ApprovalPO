using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using ApprovalPO.Services.Orders;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class ApprovalsStatusModel : PageModel
{
    private readonly IModuleAccessService _modules;
    private readonly IPurchaseOrderCatalog _purchase;
    private readonly ISalesOrderCatalog _sales;
    private readonly ILogger<ApprovalsStatusModel> _logger;

    public ApprovalsStatusModel(
        IModuleAccessService modules,
        IPurchaseOrderCatalog purchase,
        ISalesOrderCatalog sales,
        ILogger<ApprovalsStatusModel> logger)
    {
        _modules = modules;
        _purchase = purchase;
        _sales = sales;
        _logger = logger;
    }

    public string StatusFilter { get; private set; } = "Pending";
    public bool ShowPurchase { get; private set; }
    public bool ShowSales { get; private set; }
    public IReadOnlyList<PurchaseOrderRow> PurchaseRows { get; private set; } = Array.Empty<PurchaseOrderRow>();
    public IReadOnlyList<SalesOrderRow> SalesRows { get; private set; } = Array.Empty<SalesOrderRow>();
    public string? LoadError { get; private set; }

    public async Task OnGetAsync(string? status, CancellationToken cancellationToken)
    {
        StatusFilter = NormalizeStatus(status);
        var flags = await _modules.GetDashboardModulesAsync(User, cancellationToken).ConfigureAwait(false);
        ShowPurchase = flags.PurchaseApproval;
        ShowSales = flags.SalesApproval;

        if (!ShowPurchase && !ShowSales)
        {
            LoadError = "Purchase and Sales approvals are not enabled for this account.";
            return;
        }

        try
        {
            if (ShowPurchase)
            {
                var all = await _purchase.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
                PurchaseRows = all
                    .Where(r => StatusMatches(r.Status, StatusFilter))
                    .OrderByDescending(r => r.OrderDate)
                    .ThenBy(r => r.PoNumber, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (ShowSales)
            {
                var all = await _sales.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
                SalesRows = all
                    .Where(r => StatusMatches(r.Status, StatusFilter))
                    .OrderByDescending(r => r.OrderDate)
                    .ThenBy(r => r.SoNumber, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Approvals status overview failed for {Status}.", StatusFilter);
            LoadError = CatalogPageErrors.Format(ex, "approvals");
        }
    }

    public static string ProgressLabel(PurchaseOrderRow row) =>
        string.IsNullOrWhiteSpace(row.Status) ? "Pending" : row.Status.Trim();

    public static string ProgressLabel(SalesOrderRow row) =>
        string.IsNullOrWhiteSpace(row.Status) ? "Pending" : row.Status.Trim();

    private static string NormalizeStatus(string? status)
    {
        var s = (status ?? "").Trim();
        if (s.Equals("Approved", StringComparison.OrdinalIgnoreCase)) return "Approved";
        if (s.Equals("Rejected", StringComparison.OrdinalIgnoreCase)) return "Rejected";
        if (s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
        return "Pending";
    }

    private static bool StatusMatches(string? rowStatus, string wanted) =>
        string.Equals((rowStatus ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase);
}
