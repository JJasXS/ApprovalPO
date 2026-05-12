namespace ApprovalPO.Models;

public class PurchaseOrderRow
{
    /// <summary><c>PH_PO</c> header key; used to load <c>PH_PODTL</c> lines.</summary>
    public int DocKey { get; set; }

    public string PoNumber { get; set; } = "";
    public string Vendor { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>Derived from <c>PH_PO.UDF_POSTATUS</c> for JSON: null = pending, true = approved, false = cancelled (Rejected tab).</summary>
    public bool? Transferable { get; set; }

    /// <summary>Pending / Approved / Rejected (UI shows Rejected as Cancelled); from <c>UDF_POSTATUS</c> when using default list SQL.</summary>
    public string Status { get; set; } = "Pending";
    public string Description { get; set; } = "";

    /// <summary>Used for filters and display.</summary>
    public DateTime OrderDate { get; set; } = DateTime.UtcNow.Date;
}
