namespace ApprovalPO.Models;

/// <summary>Delivery order header from <c>SL_DO</c>.</summary>
public sealed class DeliveryOrderRow
{
    public int DocKey { get; set; }
    public string DoNumber { get; set; } = "";
    public string SoNumber { get; set; } = "";
    public string Customer { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public DateTime DoDate { get; set; }
}

/// <summary>Delivery order row for list JSON.</summary>
public sealed class DeliveryOrderListItem
{
    public int DocKey { get; set; }
    public string DoNumber { get; set; } = "";
    public string SoNumber { get; set; } = "";
    public string Customer { get; set; } = "";
    public DateTime DoDate { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>One line from <c>SL_DODTL</c>.</summary>
public sealed class DeliveryOrderLineRow
{
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal ReceiveQty { get; set; }
    public decimal ReturnQty { get; set; }
}
