using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using ApprovalPO.Services.Orders;

namespace ApprovalPO.Services;

public sealed record DashboardFigure(string Key, string Label, string Href, int? Count, decimal? Amount);

public sealed record DashboardTrendSeries(string Key, string Label, string Color, IReadOnlyList<int> Values);

public sealed record DashboardTrend(IReadOnlyList<string> Labels, IReadOnlyList<DashboardTrendSeries> Series);

public sealed record DashboardStatusKpi(string Key, string Label, int Count, string Href, string Tone);

public sealed record DashboardActivityItem(
    string Kind,
    string Reference,
    string Status,
    string Href,
    DateTime At);

public sealed class DashboardOverview
{
    public DashboardModuleFlags Modules { get; init; } = new();
    public IReadOnlyList<DashboardFigure> Figures { get; init; } = Array.Empty<DashboardFigure>();
    public DashboardTrend Trend { get; init; } = new(Array.Empty<string>(), Array.Empty<DashboardTrendSeries>());
    public IReadOnlyList<DashboardStatusKpi> StatusKpis { get; init; } = Array.Empty<DashboardStatusKpi>();
    public IReadOnlyList<DashboardActivityItem> RecentActivity { get; init; } = Array.Empty<DashboardActivityItem>();
}

public sealed class DashboardOverviewService
{
    private const int RecentActivityLimit = 8;

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
        var purchaseRows = Array.Empty<PurchaseOrderRow>();
        var salesRows = Array.Empty<SalesOrderRow>();

        if (modules.PurchaseApproval)
        {
            var pack = await LoadPurchaseAsync(endDate, cancellationToken).ConfigureAwait(false);
            figures.Add(pack.Figure);
            series.Add(pack.Series);
            purchaseRows = pack.Rows;
        }

        if (modules.SalesApproval)
        {
            var pack = await LoadSalesAsync(endDate, cancellationToken).ConfigureAwait(false);
            figures.Add(pack.Figure);
            series.Add(pack.Series);
            salesRows = pack.Rows;
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
            Trend = new DashboardTrend(labels, series),
            StatusKpis = BuildStatusKpis(modules, purchaseRows, salesRows),
            RecentActivity = BuildRecentActivity(modules, purchaseRows, salesRows)
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

    private async Task<(DashboardFigure Figure, DashboardTrendSeries Series, PurchaseOrderRow[] Rows)> LoadPurchaseAsync(
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _purchase.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            var pending = SumPending(rows.Select(o => (o.Status, o.Amount)));
            var values = TrendValues(PurchaseMeta.Key, rows.Select(o => o.OrderDate), endDate);
            return (
                new DashboardFigure(PurchaseMeta.Key, "Purchase approvals", "/PurchaseOrders", pending.Count, pending.Amount),
                new DashboardTrendSeries(PurchaseMeta.Key, PurchaseMeta.Label, PurchaseMeta.Color, values),
                rows.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard purchase totals unavailable.");
            var empty = EmptyPack(PurchaseMeta, "Purchase approvals", "/PurchaseOrders", endDate);
            return (empty.Figure, empty.Series, Array.Empty<PurchaseOrderRow>());
        }
    }

    private async Task<(DashboardFigure Figure, DashboardTrendSeries Series, SalesOrderRow[] Rows)> LoadSalesAsync(
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _sales.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            var pending = SumPending(rows.Select(o => (o.Status, o.Amount)));
            var values = TrendValues(SalesMeta.Key, rows.Select(o => o.OrderDate), endDate);
            return (
                new DashboardFigure(SalesMeta.Key, "Sales approvals", "/SalesOrders", pending.Count, pending.Amount),
                new DashboardTrendSeries(SalesMeta.Key, SalesMeta.Label, SalesMeta.Color, values),
                rows.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard sales totals unavailable.");
            var empty = EmptyPack(SalesMeta, "Sales approvals", "/SalesOrders", endDate);
            return (empty.Figure, empty.Series, Array.Empty<SalesOrderRow>());
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
            var values = TrendValues(GoodsMeta.Key, rows.Select(r => r.GrDate), endDate);
            return (
                new DashboardFigure(GoodsMeta.Key, "Goods received", href, rows.Count, rows.Sum(r => r.Amount)),
                new DashboardTrendSeries(GoodsMeta.Key, GoodsMeta.Label, GoodsMeta.Color, values));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard goods receipt totals unavailable.");
            return EmptyPack(GoodsMeta, "Goods received", href, endDate);
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
            var values = TrendValues(DeliveryMeta.Key, rows.Select(r => r.DoDate), endDate);
            return (
                new DashboardFigure(DeliveryMeta.Key, "Delivery orders", href, rows.Count, rows.Sum(r => r.Amount)),
                new DashboardTrendSeries(DeliveryMeta.Key, DeliveryMeta.Label, DeliveryMeta.Color, values));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard delivery order totals unavailable.");
            return EmptyPack(DeliveryMeta, "Delivery orders", href, endDate);
        }
    }

    private static IReadOnlyList<DashboardStatusKpi> BuildStatusKpis(
        DashboardModuleFlags modules,
        IReadOnlyList<PurchaseOrderRow> purchaseRows,
        IReadOnlyList<SalesOrderRow> salesRows)
    {
        if (!modules.PurchaseApproval && !modules.SalesApproval)
            return Array.Empty<DashboardStatusKpi>();

        var statuses = purchaseRows.Select(r => r.Status)
            .Concat(salesRows.Select(r => r.Status))
            .ToList();
        var pending = CountStatus(statuses, "Pending");
        var approved = CountStatus(statuses, "Approved");
        var rejected = CountStatus(statuses, "Rejected");
        var primaryList = modules.PurchaseApproval ? "/PurchaseOrders" : "/SalesOrders";

        return
        [
            new DashboardStatusKpi("pending", "Pending", pending, $"{primaryList}?status=Pending", "amber"),
            new DashboardStatusKpi("approved", "Approved", approved, $"{primaryList}?status=Approved", "green"),
            new DashboardStatusKpi("rejected", "Rejected", rejected, $"{primaryList}?status=Rejected", "red")
        ];
    }

    private static IReadOnlyList<DashboardActivityItem> BuildRecentActivity(
        DashboardModuleFlags modules,
        IReadOnlyList<PurchaseOrderRow> purchaseRows,
        IReadOnlyList<SalesOrderRow> salesRows)
    {
        var items = new List<DashboardActivityItem>();
        if (modules.PurchaseApproval)
        {
            items.AddRange(purchaseRows.Select(r => new DashboardActivityItem(
                "Purchase",
                string.IsNullOrWhiteSpace(r.PoNumber) ? $"PO-{r.DocKey}" : r.PoNumber.Trim(),
                NormalizeStatus(r.Status),
                $"/PurchaseOrders?status={Uri.EscapeDataString(NormalizeStatus(r.Status))}",
                ActivityAt(r))));
        }

        if (modules.SalesApproval)
        {
            items.AddRange(salesRows.Select(r => new DashboardActivityItem(
                "Sales",
                string.IsNullOrWhiteSpace(r.SoNumber) ? $"SO-{r.DocKey}" : r.SoNumber.Trim(),
                NormalizeStatus(r.Status),
                $"/SalesOrders?status={Uri.EscapeDataString(NormalizeStatus(r.Status))}",
                r.OrderDate)));
        }

        return items
            .OrderByDescending(i => i.At)
            .ThenBy(i => i.Reference, StringComparer.OrdinalIgnoreCase)
            .Take(RecentActivityLimit)
            .ToList();
    }

    private static DateTime ActivityAt(PurchaseOrderRow row)
    {
        var stamps = new[] { row.ApprovalLevel3At, row.ApprovalLevel2At, row.ApprovalLevel1At }
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToList();
        if (stamps.Count > 0)
            return stamps.Max();
        return row.OrderDate;
    }

    private static int CountStatus(IEnumerable<string> statuses, string wanted) =>
        statuses.Count(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeStatus(string? status)
    {
        var s = (status ?? "").Trim();
        if (s.Equals("Approved", StringComparison.OrdinalIgnoreCase)) return "Approved";
        if (s.Equals("Rejected", StringComparison.OrdinalIgnoreCase)) return "Rejected";
        if (s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
        return "Pending";
    }

    private static IReadOnlyList<int> TrendValues(string key, IEnumerable<DateTime> dates, DateTime endDate)
    {
        var real = DashboardTrendBuilder.CountByDay(dates, endDate);
        var mock = DashboardTrendBuilder.BuildMockValues(key, endDate);
        return DashboardTrendBuilder.MergeRealAndMock(real, mock);
    }

    private static (DashboardFigure Figure, DashboardTrendSeries Series) EmptyPack(
        (string Key, string Label, string Color) meta,
        string figureLabel,
        string href,
        DateTime endDate)
    {
        var mock = DashboardTrendBuilder.BuildMockValues(meta.Key, endDate);
        return (
            new DashboardFigure(meta.Key, figureLabel, href, null, null),
            new DashboardTrendSeries(meta.Key, meta.Label, meta.Color, mock));
    }
}
