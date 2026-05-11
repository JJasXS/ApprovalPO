namespace ApprovalPO.Models;

/// <summary>One line from <c>PH_PODTL</c> for the purchase order line review sheet.</summary>
public sealed class PurchaseRequestLineRow
{
    public int LineNo { get; set; }

    public string ItemCode { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary><c>PH_PODTL.SQTY</c> (stock / base quantity).</summary>
    public decimal Sqty { get; set; }

    /// <summary><c>PH_PODTL.SUOMQTY</c> (selling UOM quantity).</summary>
    public decimal SuomQty { get; set; }

    /// <summary>Legacy / fallback <c>PH_PODTL.QTY</c> when present in SQL.</summary>
    public decimal Qty { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineAmount { get; set; }

    /// <summary><c>PH_PODTL.TRANSFERABLE</c>: true = line shown as ticked in review; false or null = unticked.</summary>
    public bool? Transferable { get; set; }
}
