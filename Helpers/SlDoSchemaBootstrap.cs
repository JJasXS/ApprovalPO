using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Ensures DO header/detail cross-ref UDFs exist when the company DB has none of the preferred columns.</summary>
public static class SlDoSchemaBootstrap
{
    public static async Task EnsureCrossRefColumnsAsync(
        FbConnection conn,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureRoleAsync(
            conn, "SL_DO", DocumentCrossRefColumns.SoNumberCandidates, "UDF_SONO", logger, cancellationToken)
            .ConfigureAwait(false);
        await EnsureRoleAsync(
            conn, "SL_DO", DocumentCrossRefColumns.QtNumberCandidates, "UDF_QTNO", logger, cancellationToken)
            .ConfigureAwait(false);
        await EnsureRoleAsync(
            conn, "SL_DODTL", DocumentCrossRefColumns.SoNumberCandidates, "UDF_SONO", logger, cancellationToken)
            .ConfigureAwait(false);
        await EnsureRoleAsync(
            conn, "SL_DODTL", DocumentCrossRefColumns.QtNumberCandidates, "UDF_QTNO", logger, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureRoleAsync(
        FbConnection conn,
        string table,
        string[] candidates,
        string createColumn,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var cols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, table, cancellationToken)
            .ConfigureAwait(false);
        if (FirebirdSchemaHelper.PickColumn(cols, candidates) is not null)
            return;

        await FirebirdSchemaHelper.EnsureVarcharColumnAsync(
            conn, table, createColumn, 40, logger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
