using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Ensures <c>SL_SO.UDF_SOSTATUS</c> exists for sales-order approval tabs.</summary>
public static class SlSoSchemaBootstrap
{
    public const string StatusColumnName = "UDF_SOSTATUS";

    public static Task<bool> EnsureUdfSoStatusColumnAsync(
        FbConnection conn,
        ILogger? logger = null,
        CancellationToken cancellationToken = default) =>
        FirebirdSchemaHelper.EnsureVarcharColumnAsync(
            conn,
            "SL_SO",
            StatusColumnName,
            40,
            logger,
            ApprovalListStatusHelper.PendingDb,
            cancellationToken);

    public static string? PickStatusColumn(HashSet<string> soCols) =>
        FirebirdSchemaHelper.PickColumn(
            soCols,
            StatusColumnName,
            "UDF_SO_STATUS",
            "SOSTATUS",
            "UDF_STATUS");
}
