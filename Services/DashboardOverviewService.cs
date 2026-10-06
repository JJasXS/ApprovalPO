using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using ApprovalPO.Services.Orders;

namespace ApprovalPO.Services;

public sealed record DashboardFigure(string Key, string Label, string Href, int? Count, decimal? Amount);

public sealed class DashboardOverview
{
    public DashboardModuleFlags Modules { get; init; } = new();
    public IReadOnlyList<DashboardFigure> Figures { get; init; } = Array.Empty<DashboardFigure>();
}

public sealed class DashboardOverviewService
{
    private readonly IModuleAccessService _modules;
    private readonly IPurchaseOrderCatalog _purchase;
    private readonly ISalesOrderCatalog _sales;
    private readonly IGoodsReceiptCatalog _receipts;
    private readonly ILogger<DashboardOverviewService> _logger;

    public DashboardOverviewService(
        IModuleAccessService modules,
        IPurchaseOrderCatalog purchase,
        ISalesOrderCatalog sales,
        IGoodsReceiptCatalog receipts,
        ILogger<DashboardOverviewService> logger)
    {
        _modules = modules;
        _purchase = purchase;
        _sales = sales;
        _receipts = receipts;
        _logger = logger;
    }

    public async Task<DashboardOverview> LoadAsync(System.Security.Claims.ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var modules = await _modules.GetDashboardModulesAsync(user, cancellationToken).ConfigureAwait(false);
        var jobs = BuildJobs(modules, cancellationToken);
        var figures = jobs.Count == 0
            ? Array.Empty<DashboardFigure>()
            : await Task.WhenAll(jobs).ConfigureAwait(false);
        return new DashboardOverview { Modules = modules, Figures = figures };
    }

    public static (int Count, decimal Amount) SumPending(IEnumerable<(string Status, decimal Amount)> rows)
    {
        var count = 0;
        decimal amount = 0;
        foreach (var row in rows)
        {
            if (!string.Equals(row.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                continue;
            count++;
            amount += row.Amount;
        }
        return (count, amount);
    }

    private List<Task<DashboardFigure>> BuildJobs(DashboardModuleFlags modules, CancellationToken cancellationToken)
    {
        var jobs = new List<Task<DashboardFigure>>();
        if (modules.PurchaseApproval)
            jobs.Add(PurchaseAsync(cancellationToken));
        if (modules.SalesApproval)
            jobs.Add(SalesAsync(cancellationToken));
        if (modules.ScanPo || modules.ReceivedGoods)
            jobs.Add(GoodsAsync(modules.ScanPo ? "/ScanPO" : "/ReceivedGoods", cancellationToken));
        return jobs;
    }

    private async Task<DashboardFigure> PurchaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _purchase.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            var pending = SumPending(rows.Select(o => (o.Status, o.Amount)));
            return new DashboardFigure("purchase", "Purchase approvals", "/PurchaseOrders", pending.Count, pending.Amount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard purchase totals unavailable.");
            return new DashboardFigure("purchase", "Purchase approvals", "/PurchaseOrders", null, null);
        }
    }

    private async Task<DashboardFigure> SalesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _sales.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            var pending = SumPending(rows.Select(o => (o.Status, o.Amount)));
            return new DashboardFigure("sales", "Sales approvals", "/SalesOrders", pending.Count, pending.Amount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard sales totals unavailable.");
            return new DashboardFigure("sales", "Sales approvals", "/SalesOrders", null, null);
        }
    }

    private async Task<DashboardFigure> GoodsAsync(string href, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _receipts.GetReceiptsAsync(cancellationToken).ConfigureAwait(false);
            return new DashboardFigure("goods", "Goods received", href, rows.Count, rows.Sum(r => r.Amount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard goods receipt totals unavailable.");
            return new DashboardFigure("goods", "Goods received", href, null, null);
        }
    }
}
