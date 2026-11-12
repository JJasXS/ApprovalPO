using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Resolves parent SO/PO and upstream QT/PQ document numbers for DO/GR inserts.</summary>
public static class DocumentCrossRefResolver
{
    public static string? ReadUpstreamFromRow(
        IReadOnlyDictionary<string, object?> row,
        HashSet<string> cols,
        string fromDocType)
    {
        var col = DocumentCrossRefColumns.PickUpstreamNumber(cols, fromDocType);
        if (col is null)
            return null;
        return FirebirdRowHelpers.Clean(row.GetValueOrDefault(col));
    }

    public static async Task<string?> ResolveUpstreamAsync(
        FbConnection conn,
        FbTransaction? tx,
        string fromDocType,
        IReadOnlyDictionary<string, object?> sourceHeader,
        IReadOnlyDictionary<string, object?> sourceLine,
        HashSet<string> sourceHeaderCols,
        HashSet<string> sourceDetailCols,
        Dictionary<int, string?> docNoCache,
        CancellationToken cancellationToken = default)
    {
        var fromLine = ReadUpstreamFromRow(sourceLine, sourceDetailCols, fromDocType);
        if (!string.IsNullOrEmpty(fromLine))
            return fromLine;

        var fromHeader = ReadUpstreamFromRow(sourceHeader, sourceHeaderCols, fromDocType);
        if (!string.IsNullOrEmpty(fromHeader))
            return fromHeader;

        return await ResolveUpstreamFromLineageAsync(
            conn, tx, fromDocType, sourceLine, docNoCache, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string?> ResolveUpstreamFromLineageAsync(
        FbConnection conn,
        FbTransaction? tx,
        string fromDocType,
        IReadOnlyDictionary<string, object?> sourceLine,
        Dictionary<int, string?> docNoCache,
        CancellationToken cancellationToken)
    {
        var fromKey = FirebirdRowHelpers.GetInt(sourceLine, "FROMDOCKEY");
        if (fromKey <= 0)
            return null;

        var fromType = (FirebirdRowHelpers.Clean(sourceLine.GetValueOrDefault("FROMDOCTYPE")) ?? "")
            .Trim()
            .ToUpperInvariant();
        if (!IsUpstreamDocType(fromDocType, fromType))
            return null;

        if (docNoCache.TryGetValue(fromKey, out var cached))
            return cached;

        var table = fromDocType == SqlAccountingDocTypes.SalesOrder ? "SL_QT" : "PH_PQ";
        var docNo = await LookupDocNoAsync(conn, tx, table, fromKey, cancellationToken).ConfigureAwait(false);
        docNoCache[fromKey] = docNo;
        return docNo;
    }

    private static bool IsUpstreamDocType(string fromDocType, string lineFromType)
    {
        if (fromDocType == SqlAccountingDocTypes.SalesOrder)
            return lineFromType is SqlAccountingDocTypes.Quotation or "QT";
        return lineFromType is SqlAccountingDocTypes.PurchaseQuotation or "PQ" or "PR";
    }

    private static async Task<string?> LookupDocNoAsync(
        FbConnection conn,
        FbTransaction? tx,
        string table,
        int docKey,
        CancellationToken cancellationToken)
    {
        if (!await FirebirdSchemaHelper.RelationExistsAsync(conn, table, cancellationToken).ConfigureAwait(false))
            return null;

        var cols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, table, cancellationToken)
            .ConfigureAwait(false);
        var keyCol = FirebirdSchemaHelper.PickColumn(cols, "DOCKEY", "ID");
        var docNoCol = FirebirdSchemaHelper.PickColumn(cols, "DOCNO");
        if (keyCol is null || docNoCol is null)
            return null;

        await using var cmd = tx is null
            ? new FbCommand($"SELECT FIRST 1 {docNoCol} FROM {table} WHERE {keyCol} = @K", conn)
            : new FbCommand($"SELECT FIRST 1 {docNoCol} FROM {table} WHERE {keyCol} = @K", conn, tx);
        cmd.Parameters.Add("@K", FbDbType.Integer).Value = docKey;
        var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return FirebirdRowHelpers.Clean(obj);
    }

    public static void ApplyToRow(
        Dictionary<string, object?> values,
        string? parentCol,
        string? upstreamCol,
        string? ownDocCol,
        string parentNo,
        string? upstreamNo,
        string? ownDocNo)
    {
        if (parentCol is not null && !string.IsNullOrEmpty(parentNo))
            values[parentCol] = parentNo;
        if (upstreamCol is not null && !string.IsNullOrEmpty(upstreamNo))
            values[upstreamCol] = upstreamNo;
        if (ownDocCol is not null && !string.IsNullOrEmpty(ownDocNo))
            values[ownDocCol] = ownDocNo;
    }
}
