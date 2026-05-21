namespace ApprovalPO.Models;

public class SalesOrderRow
{
    /// <summary><c>SL_SO</c> header key; used to load <c>SL_SODTL</c> lines.</summary>
    public int DocKey { get; set; }

    public string SoNumber { get; set; } = "";

    public string Customer { get; set; } = "";

    public decimal Amount { get; set; }

    /// <summary>Derived from <c>SL_SO.UDF_SOSTATUS</c> for JSON: null = pending, true = approved, false = not pending.</summary>
    public bool? Transferable { get; set; }

    /// <summary>Pending / Approved / Cancelled / Rejected — from <c>UDF_SOSTATUS</c>.</summary>
    public string Status { get; set; } = "Pending";

    public string Description { get; set; } = "";

    public DateTime OrderDate { get; set; } = DateTime.UtcNow.Date;
}
