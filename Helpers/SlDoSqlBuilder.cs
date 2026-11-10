namespace ApprovalPO.Helpers;

/// <summary>Dynamic SQL for delivery order tables (<c>SL_DO</c>, <c>SL_DODTL</c>).</summary>
public static class SlDoSqlBuilder
{
    public static string BuildHeadersSql(
        HashSet<string> doCols,
        HashSet<string>? doDtlCols = null,
        HashSet<string>? xtransCols = null)
    {
        var key = "do:hdr:" + FirebirdCatalogSqlCache.Fingerprint(doCols, doDtlCols, xtransCols);
        return FirebirdCatalogSqlCache.GetOrAdd(key, () => BuildHeadersSqlCore(doCols, doDtlCols, xtransCols));
    }

    public static string BuildLinesSql(HashSet<string> dtlCols)
    {
        var receiveCol = FirebirdSchemaHelper.PickColumn(dtlCols, "RECEIVEQTY", "RECIEVEQTY", "RECEIVEDQTY") ?? "RECEIVEQTY";
        var returnCol = FirebirdSchemaHelper.PickColumn(dtlCols, "RETURNQTY") ?? "RETURNQTY";
        var key = $"do:ln:{receiveCol}:{returnCol}";
        return FirebirdCatalogSqlCache.GetOrAdd(key, () => BuildLinesSqlCore(receiveCol, returnCol));
    }

    private static string BuildHeadersSqlCore(
        HashSet<string> doCols,
        HashSet<string>? doDtlCols,
        HashSet<string>? xtransCols)
    {
        var (soExpr, joinSql) = BuildSoNumberLookup(doCols, doDtlCols, xtransCols);

        return $"""
            SELECT FIRST 200
              H.DOCKEY,
              TRIM(H.DOCNO) AS DONUMBER,
              {soExpr} AS SONUMBER,
              TRIM(COALESCE(H.COMPANYNAME, H.CODE, '')) AS CUSTOMER,
              COALESCE(H.DOCAMT, 0) AS AMOUNT,
              TRIM(COALESCE(CAST(H.DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
              COALESCE(H.DOCDATE, CURRENT_DATE) AS DODATE
            FROM SL_DO H
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
            FROM SL_DODTL D
            WHERE D.DOCKEY = @DocKey
            ORDER BY COALESCE(D.SEQ, 0)
            """;

    internal static (string SoExpression, string JoinSql) BuildSoNumberLookup(
        HashSet<string> doCols,
        HashSet<string>? doDtlCols,
        HashSet<string>? xtransCols)
    {
        var soSources = new List<string>();
        var joins = new List<string>();

        if (doCols.Contains("FROMDOCKEY"))
        {
            joins.Add("LEFT JOIN SL_SO S_HDR ON S_HDR.DOCKEY = H.FROMDOCKEY");
            soSources.Add("TRIM(COALESCE(S_HDR.DOCNO, ''))");
        }

        if (doDtlCols is not null && doDtlCols.Contains("FROMDOCKEY"))
        {
            var dtlTypeFilter = doDtlCols.Contains("FROMDOCTYPE")
                ? $" AND TRIM(COALESCE(D.FROMDOCTYPE, '')) = '{SqlAccountingDocTypes.SalesOrder}'"
                : string.Empty;

            joins.Add($"""
                LEFT JOIN (
                  SELECT D.DOCKEY, MIN(TRIM(SD.DOCNO)) AS SONO
                  FROM SL_DODTL D
                  INNER JOIN SL_SO SD ON SD.DOCKEY = D.FROMDOCKEY
                  WHERE COALESCE(D.FROMDOCKEY, 0) > 0
                    {dtlTypeFilter}
                  GROUP BY D.DOCKEY
                ) SO_D ON SO_D.DOCKEY = H.DOCKEY
                """);
            soSources.Add("TRIM(COALESCE(SO_D.SONO, ''))");
        }
        else if (FirebirdSchemaHelper.HasDocumentLinkColumns(xtransCols))
        {
            joins.Add($"""
                LEFT JOIN (
                  SELECT X.TODOCKEY, MIN(TRIM(SX.DOCNO)) AS SONO
                  FROM ST_XTRANS X
                  INNER JOIN SL_SO SX ON SX.DOCKEY = X.FROMDOCKEY
                  WHERE X.TODOCTYPE = '{SqlAccountingDocTypes.DeliveryOrder}'
                    AND TRIM(X.FROMDOCTYPE) = '{SqlAccountingDocTypes.SalesOrder}'
                  GROUP BY X.TODOCKEY
                ) SO_X ON SO_X.TODOCKEY = H.DOCKEY
                """);
            soSources.Add("TRIM(COALESCE(SO_X.SONO, ''))");
        }

        if (FirebirdSchemaHelper.PickColumn(doCols, "SONO", "SONUMBER", "REFDOCNO", "FROMDOCNO", "UDF_SONO") is { } soCol)
            soSources.Add($"TRIM(COALESCE(H.{soCol}, ''))");

        var joinSql = joins.Count == 0 ? string.Empty : string.Join('\n', joins);
        return (FirebirdSqlExpressions.CoalesceNonEmpty(soSources), joinSql);
    }
}
