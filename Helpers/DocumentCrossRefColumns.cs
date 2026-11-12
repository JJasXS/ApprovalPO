namespace ApprovalPO.Helpers;

/// <summary>
/// Picks DO/GR (and source SO/PO) columns that store parent / upstream document numbers.
/// Prefers existing UDF names found on Acc company DBs; falls back to native names if present.
/// </summary>
public static class DocumentCrossRefColumns
{
    public static readonly string[] SoNumberCandidates =
        ["UDF_SONO", "SONO", "SONUMBER", "REFDOCNO", "FROMDOCNO"];

    public static readonly string[] PoNumberCandidates =
        ["UDF_PONO", "PONO", "PONUMBER", "REFDOCNO", "FROMDOCNO"];

    public static readonly string[] QtNumberCandidates =
        ["UDF_QTNO", "UDF_QTNo", "QTNO", "QTNUMBER"];

    /// <summary>Includes <c>UDF_PRNO</c> for PROACC-style purchase-request numbering.</summary>
    public static readonly string[] PqNumberCandidates =
        ["UDF_PQNO", "UDF_PQNo", "UDF_PRNO", "PQNO", "PQNUMBER"];

    public static string? PickSoNumber(HashSet<string> cols) =>
        FirebirdSchemaHelper.PickColumn(cols, SoNumberCandidates);

    public static string? PickPoNumber(HashSet<string> cols) =>
        FirebirdSchemaHelper.PickColumn(cols, PoNumberCandidates);

    public static string? PickQtNumber(HashSet<string> cols) =>
        FirebirdSchemaHelper.PickColumn(cols, QtNumberCandidates);

    public static string? PickPqNumber(HashSet<string> cols) =>
        FirebirdSchemaHelper.PickColumn(cols, PqNumberCandidates);

    public static string? PickOwnDoNumber(HashSet<string> cols) =>
        FirebirdSchemaHelper.PickColumn(cols, "UDF_DONO");

    public static string? PickOwnGrNumber(HashSet<string> cols) =>
        FirebirdSchemaHelper.PickColumn(cols, "UDF_GRNO");

    public static string? PickParentNumber(HashSet<string> cols, string fromDocType) =>
        fromDocType == SqlAccountingDocTypes.SalesOrder
            ? PickSoNumber(cols)
            : PickPoNumber(cols);

    public static string? PickUpstreamNumber(HashSet<string> cols, string fromDocType) =>
        fromDocType == SqlAccountingDocTypes.SalesOrder
            ? PickQtNumber(cols)
            : PickPqNumber(cols);

    public static string? PickOwnDocNumber(HashSet<string> cols, string toDocType) =>
        toDocType == SqlAccountingDocTypes.DeliveryOrder
            ? PickOwnDoNumber(cols)
            : PickOwnGrNumber(cols);
}
