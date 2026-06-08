namespace ApprovalPO.Helpers;

/// <summary>Shared dynamic SQL for SQL Accounting document detail tables.</summary>
public static class FirebirdDocumentSqlBuilder
{
    /// <summary>
    /// Standard line list for <c>PH_PODTL</c>, <c>SL_SODTL</c>, etc.; parameter <c>@DocKey</c>.
    /// </summary>
    public static string BuildDetailLinesSql(string detailTableName)
    {
        var table = (detailTableName ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(table))
            throw new ArgumentException("Detail table name is required.", nameof(detailTableName));

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
              END AS SMALLINT) AS TRANSFERABLEINT
            FROM {table} D
            WHERE D.DOCKEY = @DocKey
            ORDER BY COALESCE(D.SEQ, 0)
            """;
    }
}
