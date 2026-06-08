namespace ApprovalPO.Helpers;

/// <summary>
/// Shared approval-tab status for purchase and sales documents
/// (<c>UDF_POSTATUS</c>, <c>UDF_SOSTATUS</c>, etc.).
/// </summary>
public static class ApprovalListStatusHelper
{
    public const string PendingDb = "PENDING";
    public const string ApprovedDb = "APPROVED";
    public const string CancelledDb = "CANCELLED";
    public const string RejectedDb = "REJECTED";

    /// <summary>Maps UI tab label to Firebird status text; null when invalid.</summary>
    public static string? ParseListStatusToDb(string? listStatus) => (listStatus ?? "").Trim() switch
    {
        var s when s.Equals("Pending", StringComparison.OrdinalIgnoreCase) => PendingDb,
        var s when s.Equals("Approved", StringComparison.OrdinalIgnoreCase) => ApprovedDb,
        var s when s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) => CancelledDb,
        var s when s.Equals("Rejected", StringComparison.OrdinalIgnoreCase) => RejectedDb,
        _ => null,
    };

    /// <summary>Maps list SQL / reader values to UI tab: Pending, Approved, Cancelled, Rejected.</summary>
    public static string NormalizeTabStatus(string? rawFromReader, bool? transferable)
    {
        if (!string.IsNullOrWhiteSpace(rawFromReader))
        {
            if (string.Equals(rawFromReader, "Pending", StringComparison.OrdinalIgnoreCase))
                return "Pending";
            if (string.Equals(rawFromReader, "Approved", StringComparison.OrdinalIgnoreCase))
                return "Approved";
            if (string.Equals(rawFromReader, "Rejected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawFromReader, RejectedDb, StringComparison.OrdinalIgnoreCase))
                return "Rejected";
            if (string.Equals(rawFromReader, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawFromReader, "Canceled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawFromReader, CancelledDb, StringComparison.OrdinalIgnoreCase))
                return "Cancelled";
        }

        return transferable switch
        {
            null => "Pending",
            true => "Approved",
            false => "Cancelled",
        };
    }

    /// <summary>SQL predicate: header is still on the Pending tab (null/blank/PENDING).</summary>
    public static string BuildPendingHeaderPredicate(string tableAlias, string statusColumn) =>
        $"""
        ({tableAlias}.{statusColumn} IS NULL
         OR TRIM(COALESCE(CAST({tableAlias}.{statusColumn} AS VARCHAR(40)), '')) = ''
         OR UPPER(TRIM(CAST({tableAlias}.{statusColumn} AS VARCHAR(40)))) = '{PendingDb}')
        """;

    /// <summary>
    /// Builds <c>TRANSFERABLEINT</c> and human-readable status label expressions from a status column.
    /// </summary>
    public static (string TransferableIntExpr, string StatusLabelExpr) BuildStatusSelectExpressions(string? statusColumn)
    {
        if (statusColumn is null)
            return ("CAST(NULL AS SMALLINT)", "CAST('Pending' AS VARCHAR(20))");

        var statusUpper = $"UPPER(TRIM(COALESCE(CAST({statusColumn} AS VARCHAR(40)), '')))";
        var transferableIntExpr = $"""
            CAST(CASE {statusUpper}
              WHEN '{ApprovedDb}' THEN 1
              WHEN '{CancelledDb}' THEN 0
              WHEN '{RejectedDb}' THEN 0
              ELSE NULL
            END AS SMALLINT)
            """;
        var statusLabelExpr = $"""
            CAST(CASE {statusUpper}
              WHEN '{ApprovedDb}' THEN 'Approved'
              WHEN '{CancelledDb}' THEN 'Cancelled'
              WHEN '{RejectedDb}' THEN 'Rejected'
              ELSE 'Pending'
            END AS VARCHAR(20))
            """;
        return (transferableIntExpr, statusLabelExpr);
    }
}
