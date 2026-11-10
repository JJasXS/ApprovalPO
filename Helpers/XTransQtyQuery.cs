using System.Globalization;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Outstanding transfer quantities from <c>ST_XTRANS</c>.</summary>
public static class XTransQtyQuery
{
    /// <summary>
    /// Sum of transferred qty per source detail key for a source document.
    /// When <paramref name="toDocType"/> is set, only that target type is counted.
    /// </summary>
    public static async Task<Dictionary<int, decimal>> FetchExistingTransferQtyMapAsync(
        FbConnection conn,
        FbTransaction? tx,
        int fromDocKey,
        string fromDocType,
        IReadOnlyList<int> detailIds,
        HashSet<string> xtransCols,
        CancellationToken cancellationToken,
        string? toDocType = null)
    {
        var result = new Dictionary<int, decimal>();
        if (detailIds.Count == 0) return result;

        var fromDocTypeCol = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCTYPE");
        var fromDocKeyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCKEY");
        var fromDtlKey = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDTLKEY");
        var toDocTypeCol = FirebirdSchemaHelper.PickColumn(xtransCols, "TODOCTYPE");
        var qtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "QTY");
        var sqtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SQTY");
        var suomCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SUOMQTY");

        if (fromDocTypeCol is null || fromDocKeyCol is null || fromDtlKey is null
            || (qtyCol is null && sqtyCol is null && suomCol is null))
            return result;

        string quantityExpr;
        if (suomCol is not null && sqtyCol is not null && qtyCol is not null)
            quantityExpr = $"COALESCE(NULLIF({suomCol}, 0), NULLIF({sqtyCol}, 0), COALESCE({qtyCol}, 0), 0)";
        else if (sqtyCol is not null && qtyCol is not null)
            quantityExpr = $"COALESCE(NULLIF({sqtyCol}, 0), COALESCE({qtyCol}, 0), 0)";
        else
            quantityExpr = suomCol ?? sqtyCol ?? qtyCol!;

        var placeholders = string.Join(", ", detailIds.Select((_, i) => $"@D{i}"));
        var toFilter = !string.IsNullOrWhiteSpace(toDocType) && toDocTypeCol is not null
            ? $" AND {toDocTypeCol} = @ToType"
            : "";

        var sql = $"""
            SELECT {fromDtlKey}, SUM(CAST({quantityExpr} AS DOUBLE PRECISION))
            FROM ST_XTRANS
            WHERE {fromDocTypeCol} = @FromType
              AND {fromDocKeyCol} = @FromKey
              AND {fromDtlKey} IN ({placeholders})
              {toFilter}
            GROUP BY {fromDtlKey}
            """;

        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@FromType", FbDbType.VarChar).Value = fromDocType;
        cmd.Parameters.Add("@FromKey", FbDbType.Integer).Value = fromDocKey;
        if (!string.IsNullOrWhiteSpace(toDocType) && toDocTypeCol is not null)
            cmd.Parameters.Add("@ToType", FbDbType.VarChar).Value = toDocType;
        for (var i = 0; i < detailIds.Count; i++)
            cmd.Parameters.Add($"@D{i}", FbDbType.Integer).Value = detailIds[i];

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var key = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var qty = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            result[key] = FirebirdRowHelpers.Money(qty);
        }

        return result;
    }

    /// <summary>Sum of transferred qty for every detail line of a source document (any TO type, or filtered).</summary>
    public static async Task<Dictionary<int, decimal>> FetchByFromDocAsync(
        FbConnection conn,
        int fromDocKey,
        string fromDocType,
        HashSet<string> xtransCols,
        CancellationToken cancellationToken,
        string? toDocType = null,
        FbTransaction? tx = null)
    {
        var fromDocTypeCol = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCTYPE");
        var fromDocKeyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCKEY");
        var fromDtlKey = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDTLKEY");
        var toDocTypeCol = FirebirdSchemaHelper.PickColumn(xtransCols, "TODOCTYPE");
        var qtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "QTY");
        var sqtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SQTY");
        var suomCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SUOMQTY");

        var result = new Dictionary<int, decimal>();
        if (fromDocTypeCol is null || fromDocKeyCol is null || fromDtlKey is null
            || (qtyCol is null && sqtyCol is null && suomCol is null))
            return result;

        string quantityExpr;
        if (suomCol is not null && sqtyCol is not null && qtyCol is not null)
            quantityExpr = $"COALESCE(NULLIF({suomCol}, 0), NULLIF({sqtyCol}, 0), COALESCE({qtyCol}, 0), 0)";
        else if (sqtyCol is not null && qtyCol is not null)
            quantityExpr = $"COALESCE(NULLIF({sqtyCol}, 0), COALESCE({qtyCol}, 0), 0)";
        else
            quantityExpr = suomCol ?? sqtyCol ?? qtyCol!;

        var toFilter = !string.IsNullOrWhiteSpace(toDocType) && toDocTypeCol is not null
            ? $" AND {toDocTypeCol} = @ToType"
            : "";

        var sql = $"""
            SELECT {fromDtlKey}, SUM(CAST({quantityExpr} AS DOUBLE PRECISION))
            FROM ST_XTRANS
            WHERE {fromDocTypeCol} = @FromType
              AND {fromDocKeyCol} = @FromKey
              {toFilter}
            GROUP BY {fromDtlKey}
            """;

        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@FromType", FbDbType.VarChar).Value = fromDocType;
        cmd.Parameters.Add("@FromKey", FbDbType.Integer).Value = fromDocKey;
        if (!string.IsNullOrWhiteSpace(toDocType) && toDocTypeCol is not null)
            cmd.Parameters.Add("@ToType", FbDbType.VarChar).Value = toDocType;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var key = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var qty = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            result[key] = FirebirdRowHelpers.Money(qty);
        }

        return result;
    }
}
