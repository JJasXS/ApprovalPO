namespace ApprovalPO.Authorization;

/// <summary>
/// Canonical authorization policy names (kept in one place so the Razor Page
/// conventions in <c>Program.cs</c> and any <c>[Authorize(Policy = ...)]</c>
/// attributes stay in sync).
/// </summary>
public static class PolicyNames
{
    /// <summary>Admin OR Maintenance role required (the Maintenance Scanner module).</summary>
    public const string MaintenanceAccess = "MaintenanceAccess";
}
