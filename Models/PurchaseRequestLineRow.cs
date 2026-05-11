namespace ApprovalPO.Models;

/// <summary>One line from <c>PH_PQDTL</c> for the purchase request review sheet.</summary>
public sealed class PurchaseRequestLineRow
{
    public int LineNo { get; set; }

    public string ItemCode { get; set; } = "";

    public string Description { get; set; } = "";

    public decimal Qty { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineAmount { get; set; }

    /// <summary><c>PH_PQDTL.TRANSFERABLE</c>: true = line shown as ticked in review; false or null = unticked.</summary>
    public bool? Transferable { get; set; }
}
