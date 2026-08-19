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

        var approvalCols = BuildApprovalSelectColumns(poCols);

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
              {approvalCols}
            FROM PH_PO
            ORDER BY DOCDATE DESC NULLS LAST, DOCNO DESC
            """;
    }

    private static string BuildApprovalSelectColumns(HashSet<string> poCols)
    {
        var parts = new List<string>();
        foreach (var (name, alias) in new (string Col, string Alias)[]
        {
            ("UDF_POAPPL1", "APPROVALLEVEL1"),
            ("UDF_POAPPL1DT", "APPROVALLEVEL1AT"),
            ("UDF_POAPPL2", "APPROVALLEVEL2"),
            ("UDF_POAPPL2DT", "APPROVALLEVEL2AT"),
            ("UDF_POAPPL3", "APPROVALLEVEL3"),
            ("UDF_POAPPL3DT", "APPROVALLEVEL3AT"),
        })
        {
            if (!poCols.Contains(name))
                continue;
            parts.Add(name.EndsWith("DT", StringComparison.Ordinal)
                ? $"{name} AS {alias}"
                : $"TRIM(COALESCE(CAST({name} AS VARCHAR(80)), '')) AS {alias}");
        }

        return parts.Count == 0 ? "" : ",\n              " + string.Join(",\n              ", parts);
    }

    /// <summary>Detail lines for <c>PH_PODTL</c>; includes <c>PROJECT</c> when the column exists.</summary>
    public static string BuildDetailLinesSql(HashSet<string> podtlCols)
    {
        var projectCol = FirebirdSchemaHelper.PickColumn(podtlCols, "PROJECT", "UDF_PROJECT");
        var projectExpr = projectCol is not null
            ? $"TRIM(COALESCE(D.{projectCol}, '')) AS PROJECT"
            : "CAST('' AS VARCHAR(40)) AS PROJECT";

        return $"""
            SELECT
              COALESCE(D.SEQ, 0) AS LINENO,
              TRIM(COALESCE(D.ITEMCODE, '')) AS ITEMCODE,
              TRIM(COALESCE(CAST(D.DESCRIPTION AS VARCHAR(800)), '')) AS DESCRIPTION,
              COALESCE(D.SQTY, 0) AS SQTY,
              COALESCE(D.SUOMQTY, 0) AS SUOMQTY,
              COALESCE(D.QTY, 0) AS QTY,
              COALESCE(D.UNITPRICE, 0) AS UNITPRICE,
              COALESCE(D.AMOUNT, 0) AS LINEAMOUNT,
              CAST(CASE
                WHEN D.TRANSFERABLE IS NULL THEN NULL
                WHEN D.TRANSFERABLE IS TRUE THEN 1
                WHEN D.TRANSFERABLE IS FALSE THEN 0
                ELSE NULL
              END AS SMALLINT) AS TRANSFERABLEINT,
              {projectExpr}
            FROM PH_PODTL D
            WHERE D.DOCKEY = @DocKey
            ORDER BY COALESCE(D.SEQ, 0)
            """;
    }
}
