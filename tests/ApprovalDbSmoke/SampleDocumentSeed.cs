using System.Globalization;
using ApprovalPO.Helpers;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Tests.ApprovalDbSmoke;

/// <summary>Inserts one pending purchase order and one pending sales order so the database checks have a document to exercise.</summary>
internal static class SampleDocumentSeed
{
    public static async Task CreateAsync(SmokeHost host)
    {
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "seed sample documents").ConfigureAwait(false);
        var poCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO").ConfigureAwait(false);
        var soCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SO").ConfigureAwait(false);
        var poDtlCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PODTL").ConfigureAwait(false);
        var soDtlCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SODTL").ConfigureAwait(false);
        var poLens = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "PH_PO").ConfigureAwait(false);
        var soLens = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "SL_SO").ConfigureAwait(false);
        var poDtlLens = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "PH_PODTL").ConfigureAwait(false);
        var soDtlLens = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "SL_SODTL").ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
        var poKey = await InsertHeaderAsync(conn, tx, "PH_PO", "SMOKE-PO-001", "SMOKE", "Smoke Test Vendor", 50_000m, "UDF_POSTATUS", poCols, poLens).ConfigureAwait(false);
        var soKey = await InsertHeaderAsync(conn, tx, "SL_SO", "SMOKE-SO-001", "SMOKE", "Smoke Test Customer", 5_000m, "UDF_SOSTATUS", soCols, soLens).ConfigureAwait(false);
        await InsertLineAsync(conn, tx, "PH_PODTL", poKey, 50_000m, poDtlCols, poDtlLens).ConfigureAwait(false);
        await InsertLineAsync(conn, tx, "SL_SODTL", soKey, 5_000m, soDtlCols, soDtlLens).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        Console.WriteLine($"Seeded PH_PO SMOKE-PO-001 dockey={poKey} and SL_SO SMOKE-SO-001 dockey={soKey}.");
    }

    private static async Task<long> InsertHeaderAsync(
        FbConnection conn, FbTransaction tx, string table, string docNo, string code, string company, decimal amount,
        string statusColumn, HashSet<string> columns, Dictionary<string, int> lengths)
    {
        var existing = await FindDocKeyAsync(conn, tx, table, docNo).ConfigureAwait(false);
        if (existing is not null)
        {
            Console.WriteLine($"{table} {docNo} already exists (dockey={existing}).");
            return existing.Value;
        }

        var key = await FirebirdTableWriter.NextKeyAsync(
            conn, table, "DOCKEY", [$"GEN_{table}_ID", $"GEN_{table}_DOCKEY", $"GEN_{table}"], default, tx).ConfigureAwait(false);
        var today = DateTime.Today;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOCKEY"] = key,
            ["DOCNO"] = docNo,
            ["DOCNOEX"] = docNo,
            ["DOCDATE"] = today,
            ["POSTDATE"] = today,
            ["TAXDATE"] = today,
            ["CODE"] = code,
            ["COMPANYNAME"] = company,
            ["DESCRIPTION"] = "Approval database smoke document",
            ["DOCAMT"] = amount,
            ["LOCALDOCAMT"] = amount,
            ["CURRENCYRATE"] = 1m,
            ["PROJECT"] = "----",
            [statusColumn] = "PENDING"
        };
        await FillRequiredAsync(conn, tx, table, columns, values).ConfigureAwait(false);
        await FirebirdTableWriter.InsertDynamicAsync(conn, table, FirebirdTableWriter.FitStrings(values, lengths), columns, default, tx).ConfigureAwait(false);
        return key;
    }

    private static async Task InsertLineAsync(
        FbConnection conn, FbTransaction tx, string table, long docKey, decimal amount,
        HashSet<string> columns, Dictionary<string, int> lengths)
    {
        if (await LineExistsAsync(conn, tx, table, docKey).ConfigureAwait(false))
            return;

        var dtlKey = await FirebirdTableWriter.NextGlobalDtlKeyAsync(conn, "DTLKEY", default, tx).ConfigureAwait(false);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DTLKEY"] = dtlKey,
            ["DOCKEY"] = docKey,
            ["SEQ"] = 1,
            ["ITEMCODE"] = "SMOKE",
            ["DESCRIPTION"] = "Smoke line",
            ["QTY"] = 1m,
            ["SQTY"] = 1m,
            ["SUOMQTY"] = 1m,
            ["UNITPRICE"] = amount,
            ["AMOUNT"] = amount,
            ["LOCALAMOUNT"] = amount,
            ["UOM"] = "UNIT",
            ["PROJECT"] = "----",
            ["TRANSFERABLE"] = false
        };
        await FillRequiredAsync(conn, tx, table, columns, values).ConfigureAwait(false);
        await FirebirdTableWriter.InsertDynamicAsync(conn, table, FirebirdTableWriter.FitStrings(values, lengths), columns, default, tx).ConfigureAwait(false);
    }

    private static async Task FillRequiredAsync(
        FbConnection conn, FbTransaction tx, string table, HashSet<string> columns, Dictionary<string, object?> values)
    {
        const string sql = """
            SELECT TRIM(RF.RDB$FIELD_NAME), F.RDB$FIELD_TYPE
            FROM RDB$RELATION_FIELDS RF
            JOIN RDB$FIELDS F ON F.RDB$FIELD_NAME = RF.RDB$FIELD_SOURCE
            WHERE TRIM(RF.RDB$RELATION_NAME) = @T
              AND COALESCE(RF.RDB$NULL_FLAG, 0) = 1
              AND F.RDB$COMPUTED_BLR IS NULL
            """;
        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@T", FbDbType.VarChar).Value = table;
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader.GetString(0).Trim();
            if (!columns.Contains(name) || values.ContainsKey(name)) continue;
            values[name] = DefaultValue(Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
        }
    }

    private static object DefaultValue(int fieldType) => fieldType switch
    {
        7 or 8 or 16 => 0,
        10 or 27 => 0d,
        12 or 13 or 35 => DateTime.Today,
        23 => false,
        _ => "-"
    };

    private static async Task<long?> FindDocKeyAsync(FbConnection conn, FbTransaction tx, string table, string docNo)
    {
        await using var cmd = new FbCommand($"SELECT FIRST 1 DOCKEY FROM {table} WHERE TRIM(DOCNO) = @N", conn, tx);
        cmd.Parameters.Add("@N", FbDbType.VarChar).Value = docNo;
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> LineExistsAsync(FbConnection conn, FbTransaction tx, string table, long docKey)
    {
        await using var cmd = new FbCommand($"SELECT FIRST 1 1 FROM {table} WHERE DOCKEY = @D", conn, tx);
        cmd.Parameters.Add("@D", FbDbType.BigInt).Value = docKey;
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return value is not null and not DBNull;
    }
}
