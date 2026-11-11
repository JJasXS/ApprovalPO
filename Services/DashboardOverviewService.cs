using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using ApprovalPO.Services.Orders;

namespace ApprovalPO.Services;

public sealed record DashboardFigure(string Key, string Label, string Href, int? Count, decimal? Amount);

public sealed record DashboardTrendSeries(string Key, string Label, string Color, IReadOnlyList<int> Values);

public sealed record DashboardTrend(IReadOnlyList<string> Labels, IReadOnlyList<DashboardTrendSeries> Series);

public sealed class DashboardOverview
{
    public DashboardModuleFlags Modules { get; init; } = new();
    public IReadOnlyList<DashboardFigure> Figures { get; init; } = Array.Empty<DashboardFigure>();
    public DashboardTrend Trend { get; init; } = new(Array.Empty<string>(), Array.Empty<DashboardTrendSeries>());
}

public sealed class DashboardOverviewService
{
    private static readonly (string Key, string Label, string Color) PurchaseMeta = ("purchase", "Purchase", "#2563eb");
    private static readonly (string Key, string Label, string Color) SalesMeta = ("sales", "Sales", "#16a34a");
    private static readonly (string Key, string Label, string Color) GoodsMeta = ("goods", "Goods", "#7c3aed");
    private static readonly (string Key, string Label, string Color) DeliveryMeta = ("delivery", "Delivery", "#0f766e");

    private readonly IModuleAccessService _modules;
    private readonly IPurchaseOrderCatalog _purchase;
    private readonly ISalesOrderCatalog _sales;
    private readonly IGoodsReceiptCatalog _receipts;
    private readonly IDeliveryOrderCatalog _deliveries;
    private readonly ILogger<DashboardOverviewService> _logger;

    public DashboardOverviewService(
        IModuleAccessService modules,
        IPurchaseOrderCatalog purchase,
        ISalesOrderCatalog sales,
        IGoodsReceiptCatalog receipts,
        IDeliveryOrderCatalog deliveries,
        ILogger<DashboardOverviewService> logger)
    {
        _modules = modules;
        _purchase = purchase;
        _sales = sales;
        _receipts = receipts;
        _deliveries = deliveries;
        _logger = logger;
    }

    public async Task<DashboardOverview> LoadAsync(System.Security.Claims.ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var modules = await _modules.GetDashboardModulesAsync(user, cancellationToken).ConfigureAwait(false);
        var endDate = DateTime.Today;
        var labels = DashboardTrendBuilder.BuildLabels(endDate);
        var series = new List<DashboardTrendSeries>();
        var figures = new List<DashboardFigure>();

        if (modules.PurchaseApproval)
        {
            var pack = await LoadPurchaseAsync(endDate, cancellationToken).ConfigureAwait(false);
            figures.Add(pack.Figure);
            series.Add(pack.Series);
        }

        if (modules.SalesApproval)
        {
            var pack = await LoadSalesAsync(endDate, cancellationToken).ConfigureAwait(false);
            figures.Add(pack.Figure);
            series.Add(pack.Series);
        }

        if (modules.ScanPo || modules.ReceivedGoods)
        {
            var href = modules.ScanPo ? "/ScanPO" : "/ReceivedGoods";
            var pack = await LoadGoodsAsync(href, endDate, cancellationToken).ConfigureAwait(false);
            figures.Add(pack.Figure);
            series.Add(pack.Series);
        }

        if (modules.ScanSo || modules.DeliveryOrders)
        {
            var href = modules.ScanSo ? "/ScanSO" : "/DeliveryOrders";
            var pack = await LoadDeliveryAsync(href, endDate, cancellationToken).ConfigureAwait(false);
            figures.Add(pack.Figure);
            series.Add(pack.Series);
        }

        return new DashboardOverview
        {
            Modules = modules,
            Figures = figures,
            Trend = new DashboardTrend(labels, series)
        };
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

    private async Task<(DashboardFigure Figure, DashboardTrendSeries Series)> LoadPurchaseAsync(
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _purchase.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            var pending = SumPending(rows.Select(o => (o.Status, o.Amount)));
            var values = DashboardTrendBuilder.CountByDay(rows.Select(o => o.OrderDate), endDate);
            return (
                new DashboardFigure(PurchaseMeta.Key, "Purchase approvals", "/PurchaseOrders", pending.Count, pending.Amount),
                new DashboardTrendSeries(PurchaseMeta.Key, PurchaseMeta.Label, PurchaseMeta.Color, values));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard purchase totals unavailable.");
            return EmptyPack(PurchaseMeta, "Purchase approvals", "/PurchaseOrders");
        }
    }

    private async Task<(DashboardFigure Figure, DashboardTrendSeries Series)> LoadSalesAsync(
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _sales.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            var pending = SumPending(rows.Select(o => (o.Status, o.Amount)));
            var values = DashboardTrendBuilder.CountByDay(rows.Select(o => o.OrderDate), endDate);
            return (
                new DashboardFigure(SalesMeta.Key, "Sales approvals", "/SalesOrders", pending.Count, pending.Amount),
                new DashboardTrendSeries(SalesMeta.Key, SalesMeta.Label, SalesMeta.Color, values));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard sales totals unavailable.");
            return EmptyPack(SalesMeta, "Sales approvals", "/SalesOrders");
        }
    }

    private async Task<(DashboardFigure Figure, DashboardTrendSeries Series)> LoadGoodsAsync(
        string href,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _receipts.GetReceiptsAsync(cancellationToken).ConfigureAwait(false);
            var values = DashboardTrendBuilder.CountByDay(rows.Select(r => r.GrDate), endDate);
            return (
                new DashboardFigure(GoodsMeta.Key, "Goods received", href, rows.Count, rows.Sum(r => r.Amount)),
                new DashboardTrendSeries(GoodsMeta.Key, GoodsMeta.Label, GoodsMeta.Color, values));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard goods receipt totals unavailable.");
            return EmptyPack(GoodsMeta, "Goods received", href);
        }
    }

    private async Task<(DashboardFigure Figure, DashboardTrendSeries Series)> LoadDeliveryAsync(
        string href,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _deliveries.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            var values = DashboardTrendBuilder.CountByDay(rows.Select(r => r.DoDate), endDate);
            return (
                new DashboardFigure(DeliveryMeta.Key, "Delivery orders", href, rows.Count, rows.Sum(r => r.Amount)),
                new DashboardTrendSeries(DeliveryMeta.Key, DeliveryMeta.Label, DeliveryMeta.Color, values));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard delivery order totals unavailable.");
            return EmptyPack(DeliveryMeta, "Delivery orders", href);
        }
    }

    private static (DashboardFigure Figure, DashboardTrendSeries Series) EmptyPack(
        (string Key, string Label, string Color) meta,
        string figureLabel,
        string href)
    {
        var zeros = Enumerable.Repeat(0, DashboardTrendBuilder.DayCount).ToArray();
        return (
            new DashboardFigure(meta.Key, figureLabel, href, null, null),
            new DashboardTrendSeries(meta.Key, meta.Label, meta.Color, zeros));
    }
}
