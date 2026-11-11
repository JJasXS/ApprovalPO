using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Ensures <c>SL_SO.UDF_SOSTATUS</c> and multi-level approval UDFs exist.</summary>
public static class SlSoSchemaBootstrap
{
    public const string StatusColumnName = "UDF_SOSTATUS";

    public static readonly (string Name, string SqlType)[] ApprovalSlotColumns =
    [
        ("UDF_SOAPPL1", "VARCHAR(80)"),
        ("UDF_SOAPPL1DT", "TIMESTAMP"),
        ("UDF_SOAPPL2", "VARCHAR(80)"),
        ("UDF_SOAPPL2DT", "TIMESTAMP"),
        ("UDF_SOAPPL3", "VARCHAR(80)"),
        ("UDF_SOAPPL3DT", "TIMESTAMP"),
    ];

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

    public static async Task<bool> EnsureSoApprovalSlotColumnsAsync(
        FbConnection conn,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var soCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SO", cancellationToken)
            .ConfigureAwait(false);
        var addedAny = false;
        foreach (var (name, sqlType) in ApprovalSlotColumns)
        {
            if (soCols.Contains(name))
                continue;
            try
            {
                await using var add = new FbCommand($"ALTER TABLE SL_SO ADD {name} {sqlType}", conn);
                await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                logger?.LogInformation("Added SL_SO.{Column} {Type}.", name, sqlType);
                addedAny = true;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                    continue;
                logger?.LogWarning(ex, "Could not add SL_SO.{Column}.", name);
                return false;
            }
        }

        if (addedAny)
            FirebirdSchemaHelper.InvalidateColumnCache("SL_SO");
        return true;
    }

    public static string? PickStatusColumn(HashSet<string> soCols) =>
        FirebirdSchemaHelper.PickColumn(
            soCols,
            StatusColumnName,
            "UDF_SO_STATUS",
            "SOSTATUS",
            "UDF_STATUS");
}
