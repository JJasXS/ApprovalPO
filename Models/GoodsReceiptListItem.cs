namespace ApprovalPO.Models;

/// <summary>Goods receipt row for Scan PO list JSON.</summary>
public sealed class GoodsReceiptListItem
{
    public int DocKey { get; set; }
    public string GrNumber { get; set; } = "";
    public string PoNumber { get; set; } = "";
    public string Vendor { get; set; } = "";
    public DateTime GrDate { get; set; }
    public decimal Amount { get; set; }
}
