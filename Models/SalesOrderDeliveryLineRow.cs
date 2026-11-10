namespace ApprovalPO.Models;

/// <summary>SO line with delivered / outstanding qty from <c>ST_XTRANS</c>.</summary>
public sealed class SalesOrderDeliveryLineRow
{
    public int LineNo { get; set; }
    public int DtlKey { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Project { get; set; } = "";
    public decimal OrderedQty { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal OutstandingQty { get; set; }
    public bool? Transferable { get; set; }
}
