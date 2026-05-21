namespace ApprovalPO.Models;

/// <summary>One line from <c>SL_SODTL</c> for the sales order line review sheet.</summary>
public sealed class SalesOrderLineRow
{
    public int LineNo { get; set; }

    public string ItemCode { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary><c>SL_SODTL.SQTY</c> (stock / base quantity).</summary>
    public decimal Sqty { get; set; }

    /// <summary><c>SL_SODTL.SUOMQTY</c> (selling UOM quantity).</summary>
    public decimal SuomQty { get; set; }

    /// <summary>Legacy / fallback <c>SL_SODTL.QTY</c>.</summary>
    public decimal Qty { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineAmount { get; set; }

    /// <summary>Maps to <c>SL_SODTL.TRANSFERABLE</c>.</summary>
    public bool? Transferable { get; set; }
}
