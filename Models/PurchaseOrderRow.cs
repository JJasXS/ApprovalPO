namespace ApprovalPO.Models;

public class PurchaseOrderRow
{
    public string PoNumber { get; set; } = "";
    public string Vendor { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>Null while pending; set true when approved (mock workflow).</summary>
    public bool? Transferable { get; set; }
    public string Status { get; set; } = "Pending";
    public string Description { get; set; } = "";
}
