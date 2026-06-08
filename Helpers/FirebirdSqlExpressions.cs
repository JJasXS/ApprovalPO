namespace ApprovalPO.Helpers;

/// <summary>Reusable Firebird SQL expression fragments for dynamic catalog queries.</summary>
public static class FirebirdSqlExpressions
{
    /// <summary>Prefer the first non-empty SQL fragment: <c>TRIM(COALESCE(NULLIF(a,''), NULLIF(b,''), ''))</c>.</summary>
    public static string CoalesceNonEmpty(IReadOnlyList<string> sqlFragments)
    {
        if (sqlFragments.Count == 0)
            return "CAST('' AS VARCHAR(40))";
        if (sqlFragments.Count == 1)
            return sqlFragments[0];

        var nullIfParts = sqlFragments.Select(fragment => $"NULLIF({fragment}, '')");
        return $"TRIM(COALESCE({string.Join(", ", nullIfParts)}, ''))";
    }

    /// <summary>Optional <c>FROMDOCTYPE</c> filter when the column exists on a detail table.</summary>
    public static string FromDocTypeFilter(string tableAlias, string docType = SqlAccountingDocTypes.PurchaseOrder) =>
        $"AND ({tableAlias}.FROMDOCTYPE IS NULL OR TRIM({tableAlias}.FROMDOCTYPE) = '' OR TRIM({tableAlias}.FROMDOCTYPE) = '{docType}')";
}
