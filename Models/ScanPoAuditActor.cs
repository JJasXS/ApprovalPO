namespace ApprovalPO.Models;

/// <summary>Logged-in user performing a scan PO action.</summary>
public sealed class ScanPoAuditActor
{
    public string Email { get; init; } = "";
    public string DisplayName { get; init; } = "";

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(DisplayName);
}
