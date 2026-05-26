namespace ApprovalPO.Models;

/// <summary>Goods receipt header from <c>PH_GR</c>.</summary>
public sealed class GoodsReceiptRow
{
    public int DocKey { get; set; }
    public string GrNumber { get; set; } = "";
    public string PoNumber { get; set; } = "";
    public string Vendor { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public DateTime GrDate { get; set; }
}
