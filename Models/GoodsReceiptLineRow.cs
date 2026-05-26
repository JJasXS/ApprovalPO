namespace ApprovalPO.Models;

/// <summary>Goods receipt line from <c>PH_GRDTL</c>.</summary>
public sealed class GoodsReceiptLineRow
{
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal ReceiveQty { get; set; }
    public decimal ReturnQty { get; set; }
}
