namespace ApprovalPO.Services.Scan;

/// <summary>Prefixes for scan draft/submit keys so PO and SO dockeys never collide.</summary>
public static class ScanDocKinds
{
    public const string PurchaseOrder = "PO";
    public const string SalesOrder = "SO";
}
