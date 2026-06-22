namespace ApprovalPO.Models;

/// <summary>Which home-dashboard modules are visible for the signed-in user.</summary>
public sealed class DashboardModuleFlags
{
    public bool PurchaseApproval { get; init; } = true;
    public bool SalesApproval { get; init; } = true;
    public bool ScanPo { get; init; } = true;
    public bool ReceivedGoods { get; init; } = true;
    public bool MaintenanceScanner { get; init; } = true;

    public bool HasAny =>
        PurchaseApproval || SalesApproval || ScanPo || ReceivedGoods || MaintenanceScanner;
}
