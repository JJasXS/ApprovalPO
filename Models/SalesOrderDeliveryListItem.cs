namespace ApprovalPO.Models;

/// <summary>Approved SO row for Scan SO list with delivery progress.</summary>
public sealed class SalesOrderDeliveryListItem
{
    public int DocKey { get; set; }
    public string SoNumber { get; set; } = "";
    public string Customer { get; set; } = "";
    public DateTime OrderDate { get; set; }
    public decimal Amount { get; set; }
    public decimal OrderedQty { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal OutstandingQty { get; set; }
    public int DeliveredPercent { get; set; }
    /// <summary>NotDelivered | Partial | Delivered</summary>
    public string DeliveryLabel { get; set; } = "NotDelivered";
}
