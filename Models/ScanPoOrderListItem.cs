namespace ApprovalPO.Models;

/// <summary>PO row for the scan list JSON API (ERP-approved + scan-submit flag).</summary>
public sealed class ScanPoOrderListItem
{
    public int DocKey { get; set; }
    public string PoNumber { get; set; } = "";
    public DateTime OrderDate { get; set; }
    public decimal Amount { get; set; }
    /// <summary>True after user taps Submit on the scan detail page.</summary>
    public bool ScanSubmitted { get; set; }
}
