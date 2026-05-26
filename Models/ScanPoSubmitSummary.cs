namespace ApprovalPO.Models;

/// <summary>Submitted PO summary for list display and reporting.</summary>
public sealed class ScanPoSubmitSummary
{
    public int DocKey { get; set; }
    public string PoNumber { get; set; } = "";
    public DateTime SubmittedAtUtc { get; set; }
    public string SubmittedByEmail { get; set; } = "";
    public string SubmittedByName { get; set; } = "";
}
