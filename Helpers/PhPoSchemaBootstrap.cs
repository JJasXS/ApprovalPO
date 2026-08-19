using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Ensures <c>PH_PO.UDF_POSTATUS</c> and multi-level approval UDFs exist (ApprovalPO + eQuotation db_initializer).</summary>
public static class PhPoSchemaBootstrap
{
    public const string StatusColumnName = "UDF_POSTATUS";

    public static readonly (string Name, string SqlType)[] ApprovalSlotColumns =
    [
        ("UDF_POAPPL1", "VARCHAR(80)"),
        ("UDF_POAPPL1DT", "TIMESTAMP"),
        ("UDF_POAPPL2", "VARCHAR(80)"),
        ("UDF_POAPPL2DT", "TIMESTAMP"),
        ("UDF_POAPPL3", "VARCHAR(80)"),
        ("UDF_POAPPL3DT", "TIMESTAMP"),
    ];

    public static async Task<bool> EnsureUdfPoStatusColumnAsync(
        FbConnection conn,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!await RelationExistsAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false))
        {
            logger?.LogWarning("PH_PO table not found; {Column} ensure skipped.", StatusColumnName);
            return false;
        }

        var poCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false);
        if (poCols.Contains(StatusColumnName))
            return true;

        try
        {
            await using var add = new FbCommand(
                $"ALTER TABLE PH_PO ADD {StatusColumnName} VARCHAR(40)",
                conn);
            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Added PH_PO.{Column} VARCHAR(40).", StatusColumnName);
            Console.WriteLine($"[DB INIT] {StatusColumnName} column added to PH_PO");
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            logger?.LogWarning(ex, "Could not add PH_PO.{Column}.", StatusColumnName);
            return false;
        }

        try
        {
            await using var backfill = new FbCommand(
                $"""
                UPDATE PH_PO
                SET {StatusColumnName} = 'PENDING'
                WHERE {StatusColumnName} IS NULL
                   OR TRIM(COALESCE({StatusColumnName}, '')) = ''
                """,
                conn);
            await backfill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Backfilled blank PH_PO.{Column} to PENDING.", StatusColumnName);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PH_PO.{Column} backfill skipped.", StatusColumnName);
        }

        FirebirdSchemaHelper.InvalidateColumnCache("PH_PO");
        return true;
    }

    public static async Task<bool> EnsurePoApprovalSlotColumnsAsync(
        FbConnection conn,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!await RelationExistsAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false))
        {
            logger?.LogWarning("PH_PO table not found; approval slot columns ensure skipped.");
            return false;
        }

        var poCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false);
        var addedAny = false;
        foreach (var (name, sqlType) in ApprovalSlotColumns)
        {
            if (poCols.Contains(name))
                continue;

            try
            {
                await using var add = new FbCommand($"ALTER TABLE PH_PO ADD {name} {sqlType}", conn);
                await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                logger?.LogInformation("Added PH_PO.{Column} {Type}.", name, sqlType);
                Console.WriteLine($"[DB INIT] {name} column added to PH_PO");
                addedAny = true;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                logger?.LogWarning(ex, "Could not add PH_PO.{Column}.", name);
                return false;
            }
        }

        if (addedAny)
            FirebirdSchemaHelper.InvalidateColumnCache("PH_PO");

        return true;
    }

    private static async Task<bool> RelationExistsAsync(
        FbConnection conn,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmd = new FbCommand(
            """
            SELECT 1
            FROM RDB$RELATIONS
            WHERE RDB$RELATION_NAME = @T
              AND COALESCE(RDB$SYSTEM_FLAG, 0) = 0
            """,
            conn);
        cmd.Parameters.Add("@T", FbDbType.Char).Value = tableName.Trim().ToUpperInvariant();
        var row = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return row is not null && row != DBNull.Value;
    }
}
