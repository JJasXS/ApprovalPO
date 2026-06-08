namespace ApprovalPO.Helpers;

/// <summary>Dynamic SQL for goods receipt tables (<c>PH_GR</c>, <c>PH_GRDTL</c>).</summary>
public static class PhGrSqlBuilder
{
    /// <summary>List/detail header SQL for <c>PH_GR</c>; resolves linked PO # from header, transfer, or line tables.</summary>
    public static string BuildHeadersSql(
        HashSet<string> grCols,
        HashSet<string>? grDtlCols = null,
        HashSet<string>? xtransCols = null)
    {
        var key = "gr:hdr:" + FirebirdCatalogSqlCache.Fingerprint(grCols, grDtlCols, xtransCols);
        return FirebirdCatalogSqlCache.GetOrAdd(key, () => BuildHeadersSqlCore(grCols, grDtlCols, xtransCols));
    }

    /// <summary>Line SQL for <c>PH_GRDTL</c>; parameter <c>@DocKey</c>.</summary>
    public static string BuildLinesSql(HashSet<string> dtlCols)
    {
        var receiveCol = FirebirdSchemaHelper.PickColumn(dtlCols, "RECEIVEQTY", "RECIEVEQTY", "RECEIVEDQTY") ?? "RECEIVEQTY";
        var returnCol = FirebirdSchemaHelper.PickColumn(dtlCols, "RETURNQTY") ?? "RETURNQTY";
        var key = $"gr:ln:{receiveCol}:{returnCol}";
        return FirebirdCatalogSqlCache.GetOrAdd(key, () => BuildLinesSqlCore(receiveCol, returnCol));
    }

    private static string BuildHeadersSqlCore(
        HashSet<string> grCols,
        HashSet<string>? grDtlCols,
        HashSet<string>? xtransCols)
    {
        var (poExpr, joinSql) = BuildPoNumberLookup(grCols, grDtlCols, xtransCols);

        return $"""
            SELECT FIRST 200
              H.DOCKEY,
              TRIM(H.DOCNO) AS GRNUMBER,
              {poExpr} AS PONUMBER,
              TRIM(COALESCE(H.COMPANYNAME, H.CODE, '')) AS VENDOR,
              COALESCE(H.DOCAMT, 0) AS AMOUNT,
              TRIM(COALESCE(CAST(H.DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
              COALESCE(H.DOCDATE, CURRENT_DATE) AS GRDATE
            FROM PH_GR H
            {joinSql}
            ORDER BY H.DOCDATE DESC NULLS LAST, H.DOCNO DESC
            """;
    }

    private static string BuildLinesSqlCore(string receiveCol, string returnCol) =>
        $"""
            SELECT
              COALESCE(D.SEQ, 0) AS LINENO,
              TRIM(COALESCE(D.ITEMCODE, '')) AS ITEMCODE,
              TRIM(COALESCE(CAST(D.DESCRIPTION AS VARCHAR(800)), '')) AS DESCRIPTION,
              COALESCE(D.QTY, 0) AS QTY,
              COALESCE(D.{receiveCol}, 0) AS RECEIVEQTY,
              COALESCE(D.{returnCol}, 0) AS RETURNQTY
            FROM PH_GRDTL D
            WHERE D.DOCKEY = @DocKey
            ORDER BY COALESCE(D.SEQ, 0)
            """;

    internal static (string PoExpression, string JoinSql) BuildPoNumberLookup(
        HashSet<string> grCols,
        HashSet<string>? grDtlCols,
        HashSet<string>? xtransCols)
    {
        var poSources = new List<string>();
        var joins = new List<string>();

        if (grCols.Contains("FROMDOCKEY"))
        {
            joins.Add("LEFT JOIN PH_PO P_HDR ON P_HDR.DOCKEY = H.FROMDOCKEY");
            poSources.Add("TRIM(COALESCE(P_HDR.DOCNO, ''))");
        }

        // Prefer line-level PO link (one grouped join) over per-row ST_XTRANS subqueries.
        if (grDtlCols is not null && grDtlCols.Contains("FROMDOCKEY"))
        {
            var dtlTypeFilter = grDtlCols.Contains("FROMDOCTYPE")
                ? FirebirdSqlExpressions.FromDocTypeFilter("D")
                : string.Empty;

            joins.Add($"""
                LEFT JOIN (
                  SELECT D.DOCKEY, MIN(TRIM(PD.DOCNO)) AS PONO
                  FROM PH_GRDTL D
                  INNER JOIN PH_PO PD ON PD.DOCKEY = D.FROMDOCKEY
                  WHERE COALESCE(D.FROMDOCKEY, 0) > 0
                    {dtlTypeFilter}
                  GROUP BY D.DOCKEY
                ) PO_D ON PO_D.DOCKEY = H.DOCKEY
                """);
            poSources.Add("TRIM(COALESCE(PO_D.PONO, ''))");
        }
        else if (FirebirdSchemaHelper.HasDocumentLinkColumns(xtransCols))
        {
            joins.Add($"""
                LEFT JOIN (
                  SELECT X.TODOCKEY, MIN(TRIM(PX.DOCNO)) AS PONO
                  FROM ST_XTRANS X
                  INNER JOIN PH_PO PX ON PX.DOCKEY = X.FROMDOCKEY
                  WHERE X.TODOCTYPE = '{SqlAccountingDocTypes.GoodsReceived}'
                    AND TRIM(X.FROMDOCTYPE) = '{SqlAccountingDocTypes.PurchaseOrder}'
                  GROUP BY X.TODOCKEY
                ) PO_X ON PO_X.TODOCKEY = H.DOCKEY
                """);
            poSources.Add("TRIM(COALESCE(PO_X.PONO, ''))");
        }

        if (FirebirdSchemaHelper.PickColumn(grCols, "PONO", "PONUMBER", "REFDOCNO", "FROMDOCNO") is { } poCol)
            poSources.Add($"TRIM(COALESCE(H.{poCol}, ''))");

        var joinSql = joins.Count == 0 ? string.Empty : string.Join('\n', joins);
        return (FirebirdSqlExpressions.CoalesceNonEmpty(poSources), joinSql);
    }
}
