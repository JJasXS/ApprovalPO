namespace ApprovalPO.Models;

public sealed class ScanPoSubmissionState
{
    public int DocKey { get; set; }
    public bool IsSubmitted { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string PoNumber { get; set; } = "";
    public Dictionary<string, int> ScanCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
