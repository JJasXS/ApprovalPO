namespace ApprovalPO.Models;

public class PurchaseOrderRow
{
    /// <summary>PH_PQ header key; used to load <c>PH_PQDTL</c> lines.</summary>
    public int DocKey { get; set; }

    public string PoNumber { get; set; } = "";
    public string Vendor { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>PH_PQ.TRANSFERABLE: null = pending tab, true = approved, false = cancelled.</summary>
    public bool? Transferable { get; set; }

    /// <summary>Pending / Approved / Rejected (UI shows Rejected as Cancelled); aligned with <see cref="Transferable"/> for PH_PQ.</summary>
    public string Status { get; set; } = "Pending";
    public string Description { get; set; } = "";

    /// <summary>Used for filters and display.</summary>
    public DateTime OrderDate { get; set; } = DateTime.UtcNow.Date;
}
