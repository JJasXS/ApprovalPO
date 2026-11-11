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

        var cols = ResolveCols(xtransCols);
        if (cols is null) return result;

        var quantityExpr = StockQtyExpr(cols.SqtyCol, cols.SuomCol, cols.QtyCol);
        var placeholders = string.Join(", ", detailIds.Select((_, i) => $"@D{i}"));
        var toFilter = ToTypeFilter(toDocType, cols.ToDocTypeCol);

        var sql = $"""
            SELECT {cols.FromDtlKey}, SUM(CAST({quantityExpr} AS DOUBLE PRECISION))
            FROM ST_XTRANS
            WHERE {cols.FromDocTypeCol} = @FromType
              AND {cols.FromDocKeyCol} = @FromKey
              AND {cols.FromDtlKey} IN ({placeholders})
              {toFilter}
            GROUP BY {cols.FromDtlKey}
            """;

        await using var cmd = new FbCommand(sql, conn, tx);
        BindFromDoc(cmd, fromDocType, fromDocKey, toDocType, cols.ToDocTypeCol);
        for (var i = 0; i < detailIds.Count; i++)
            cmd.Parameters.Add($"@D{i}", FbDbType.Integer).Value = detailIds[i];

        return await ReadQtyMapAsync(cmd, cancellationToken).ConfigureAwait(false);
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
        var result = new Dictionary<int, decimal>();
        var cols = ResolveCols(xtransCols);
        if (cols is null) return result;

        var quantityExpr = StockQtyExpr(cols.SqtyCol, cols.SuomCol, cols.QtyCol);
        var toFilter = ToTypeFilter(toDocType, cols.ToDocTypeCol);

        var sql = $"""
            SELECT {cols.FromDtlKey}, SUM(CAST({quantityExpr} AS DOUBLE PRECISION))
            FROM ST_XTRANS
            WHERE {cols.FromDocTypeCol} = @FromType
              AND {cols.FromDocKeyCol} = @FromKey
              {toFilter}
            GROUP BY {cols.FromDtlKey}
            """;

        await using var cmd = new FbCommand(sql, conn, tx);
        BindFromDoc(cmd, fromDocType, fromDocKey, toDocType, cols.ToDocTypeCol);
        return await ReadQtyMapAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Prefer stock <c>SQTY</c>, then <c>SUOMQTY</c>, then <c>QTY</c>.</summary>
    private static string StockQtyExpr(string? sqtyCol, string? suomCol, string? qtyCol)
    {
        if (sqtyCol is not null && suomCol is not null && qtyCol is not null)
            return $"COALESCE(NULLIF({sqtyCol}, 0), NULLIF({suomCol}, 0), COALESCE({qtyCol}, 0), 0)";
        if (sqtyCol is not null && qtyCol is not null)
            return $"COALESCE(NULLIF({sqtyCol}, 0), COALESCE({qtyCol}, 0), 0)";
        if (sqtyCol is not null && suomCol is not null)
            return $"COALESCE(NULLIF({sqtyCol}, 0), COALESCE({suomCol}, 0), 0)";
        return sqtyCol ?? qtyCol ?? suomCol!;
    }

    private static string ToTypeFilter(string? toDocType, string? toDocTypeCol) =>
        !string.IsNullOrWhiteSpace(toDocType) && toDocTypeCol is not null
            ? $" AND {toDocTypeCol} = @ToType"
            : "";

    private static void BindFromDoc(
        FbCommand cmd,
        string fromDocType,
        int fromDocKey,
        string? toDocType,
        string? toDocTypeCol)
    {
        cmd.Parameters.Add("@FromType", FbDbType.VarChar).Value = fromDocType;
        cmd.Parameters.Add("@FromKey", FbDbType.Integer).Value = fromDocKey;
        if (!string.IsNullOrWhiteSpace(toDocType) && toDocTypeCol is not null)
            cmd.Parameters.Add("@ToType", FbDbType.VarChar).Value = toDocType;
    }

    private static async Task<Dictionary<int, decimal>> ReadQtyMapAsync(
        FbCommand cmd,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, decimal>();
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

    private static XTransCols? ResolveCols(HashSet<string> xtransCols)
    {
        var fromDocTypeCol = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCTYPE");
        var fromDocKeyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCKEY");
        var fromDtlKey = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDTLKEY");
        var toDocTypeCol = FirebirdSchemaHelper.PickColumn(xtransCols, "TODOCTYPE");
        var qtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "QTY");
        var sqtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SQTY");
        var suomCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SUOMQTY");

        if (fromDocTypeCol is null || fromDocKeyCol is null || fromDtlKey is null
            || (qtyCol is null && sqtyCol is null && suomCol is null))
            return null;

        return new XTransCols(
            fromDocTypeCol,
            fromDocKeyCol,
            fromDtlKey,
            toDocTypeCol,
            qtyCol,
            sqtyCol,
            suomCol);
    }

    private sealed record XTransCols(
        string FromDocTypeCol,
        string FromDocKeyCol,
        string FromDtlKey,
        string? ToDocTypeCol,
        string? QtyCol,
        string? SqtyCol,
        string? SuomCol);
}
