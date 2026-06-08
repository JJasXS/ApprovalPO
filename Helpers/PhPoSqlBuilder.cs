namespace ApprovalPO.Helpers;

/// <summary>Dynamic SQL for purchase order tables (<c>PH_PO</c>, <c>PH_PODTL</c>).</summary>
public static class PhPoSqlBuilder
{
    public static string? PickStatusColumn(HashSet<string> poCols) =>
        FirebirdSchemaHelper.PickColumn(
            poCols,
            PhPoSchemaBootstrap.StatusColumnName,
            "UDF_PO_STATUS",
            "POSTATUS",
            "UDF_STATUS");

    /// <summary>Builds list SQL from <c>PH_PO</c> columns; uses UDF status when present.</summary>
    public static string BuildPurchaseOrdersSql(HashSet<string> poCols)
    {
        var (transferableExpr, pqStatusExpr) =
            ApprovalListStatusHelper.BuildStatusSelectExpressions(PickStatusColumn(poCols));

        return $"""
            SELECT FIRST 200
              DOCKEY,
              TRIM(DOCNO) AS PONUMBER,
              TRIM(COALESCE(COMPANYNAME, CODE, '')) AS VENDOR,
              COALESCE(DOCAMT, 0) AS AMOUNT,
              TRIM(COALESCE(CAST(DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
              COALESCE(DOCDATE, CURRENT_DATE) AS ORDERDATE,
              {transferableExpr} AS TRANSFERABLEINT,
              {pqStatusExpr} AS PQSTATUS
            FROM PH_PO
            ORDER BY DOCDATE DESC NULLS LAST, DOCNO DESC
            """;
    }
}
