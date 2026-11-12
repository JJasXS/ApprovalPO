using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Ensures GR header/detail cross-ref UDFs exist when the company DB has none of the preferred columns.</summary>
public static class PhGrSchemaBootstrap
{
    public static async Task EnsureCrossRefColumnsAsync(
        FbConnection conn,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureRoleAsync(
            conn, "PH_GR", DocumentCrossRefColumns.PoNumberCandidates, "UDF_PONO", logger, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePqRoleAsync(conn, "PH_GR", logger, cancellationToken).ConfigureAwait(false);
        await EnsureRoleAsync(
            conn, "PH_GRDTL", DocumentCrossRefColumns.PoNumberCandidates, "UDF_PONO", logger, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePqRoleAsync(conn, "PH_GRDTL", logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsurePqRoleAsync(
        FbConnection conn,
        string table,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var cols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, table, cancellationToken)
            .ConfigureAwait(false);
        if (DocumentCrossRefColumns.PickPqNumber(cols) is not null)
            return;

        await FirebirdSchemaHelper.EnsureVarcharColumnAsync(
            conn, table, "UDF_PQNO", 40, logger, cancellationToken: cancellationToken)
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
