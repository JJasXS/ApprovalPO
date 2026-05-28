namespace ApprovalPO.Services.MaintenanceScanner;

/// <summary>Result of validating a scanned stock item code.</summary>
public sealed class MaintenanceScanValidateResult
{
    public required bool Exists { get; init; }
    public string Description { get; init; } = "";
    public string LocationCode { get; init; } = "";
    public string LocationDescription { get; init; } = "";
    public string Project { get; init; } = "";
    public string LastScanned { get; init; } = "";
}

/// <summary>Inputs to insert a new ST_ITEM_TPLDTL row.</summary>
public sealed class MaintenanceScanInsertRequest
{
    public string Code { get; init; } = "";
    /// <summary>Display description selected by the user (preferred lookup).</summary>
    public string? LocationDescription { get; init; }
    /// <summary>Fallback when description match fails (rescan same location by its code).</summary>
    public string? LocationCode { get; init; }
    public string? OperatorDisplayName { get; init; }
    public string? Remark1 { get; init; }
    public string? Remark2 { get; init; }
    public string? Remark3 { get; init; }
}

/// <summary>Row written to ST_ITEM_TPLDTL.</summary>
internal sealed class StItemTpldtlInsertRow
{
    public string Code { get; init; } = "";
    public string ItemCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public string Remark1 { get; init; } = "";
    public string Remark2 { get; init; } = "";
    public string Remark3 { get; init; } = "";
    public string UdfDateTime { get; init; } = "";
    public string UdfUser { get; init; } = "";
}
