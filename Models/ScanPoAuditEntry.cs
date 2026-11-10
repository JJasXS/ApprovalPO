namespace ApprovalPO.Models;

/// <summary>One append-only audit event for scan PO workflow.</summary>
public sealed class ScanPoAuditEntry
{
    public int DocKey { get; set; }
    /// <summary><c>PO</c> or <c>SO</c> — empty legacy rows are treated as PO.</summary>
    public string DocKind { get; set; } = "PO";
    public string PoNumber { get; set; } = "";
    /// <summary><c>draft_saved</c>, <c>submitted</c>, or <c>reopened</c>.</summary>
    public string Action { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string UserEmail { get; set; } = "";
    public string UserDisplayName { get; set; } = "";
    public int? TotalScans { get; set; }
}
