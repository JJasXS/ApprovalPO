namespace ApprovalPO.Services.Orders;

/// <summary>Configures a Firebird document transfer (PO→GR, SO→DO, …).</summary>
public sealed record DocumentTransferSpec(
    string SourceHeaderTable,
    string SourceDetailTable,
    string TargetHeaderTable,
    string TargetDetailTable,
    string FromDocType,
    string ToDocType,
    string DocNoPrefix,
    string DescriptionTemplate,
    IReadOnlyList<string> TargetHeaderGenerators,
    IReadOnlyList<string> XTransGenerators,
    IReadOnlyList<string> ExtraTargetDetailQtyColumns);
