using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;

namespace ApprovalPO.Helpers;

/// <summary>
/// Ensures required <c>SY_USER</c> UDF columns exist for ApprovalPO.
/// This is required because some tenant DBs may not have the approval UDFs deployed yet.
/// </summary>
public static class SyUserSchemaBootstrap
{
    /// <summary>Approval role slot: used to derive approval authority.</summary>
    public const string AppRoleCol = "UDF_APPROLE";
    public const string AppLevelCol = "UDF_APPLEVEL";
    public const string AppFromCol = "UDF_APPFROM";
    public const string AppToCol = "UDF_APPTO";

    /// <summary>Ensures approval UDFs exist in <c>SY_USER</c> (no-op when present).</summary>
    public static async Task EnsureApprovalUdfColumnsAsync(
        FbConnection conn,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var cols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SY_USER", cancellationToken)
            .ConfigureAwait(false);

        // Add columns only when missing. We keep types permissive to avoid conversion issues.
        if (!cols.Contains(AppRoleCol))
        {
            await ExecuteAlterAsync(conn, $"ALTER TABLE SY_USER ADD {AppRoleCol} VARCHAR(80)", logger, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!cols.Contains(AppLevelCol))
        {
            await ExecuteAlterAsync(conn, $"ALTER TABLE SY_USER ADD {AppLevelCol} NUMERIC(15,2)", logger, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!cols.Contains(AppFromCol))
        {
            await ExecuteAlterAsync(conn, $"ALTER TABLE SY_USER ADD {AppFromCol} NUMERIC(15,4)", logger, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!cols.Contains(AppToCol))
        {
            await ExecuteAlterAsync(conn, $"ALTER TABLE SY_USER ADD {AppToCol} NUMERIC(15,4)", logger, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAlterAsync(
        FbConnection conn,
        string sql,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Ensuring SY_USER UDF column via: {Sql}", sql);
        await using var cmd = new FbCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // Invalidate cache since we may have added new columns.
        FirebirdSchemaHelper.InvalidateColumnCache("SY_USER");
    }
}

