using System.Globalization;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Dynamic INSERT helpers for Firebird tables (mirrors eQuotation <c>_insert_dynamic</c> / <c>_next_key</c>).</summary>
public static class FirebirdTableWriter
{
    public static string? PickColumn(HashSet<string> columns, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var u = c.Trim().ToUpperInvariant();
            if (columns.Contains(u))
                return u;
        }

        return null;
    }

    public static async Task<Dictionary<string, int>> GetStringColumnLengthsAsync(
        FbConnection conn,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TRIM(RF.RDB$FIELD_NAME) AS COL_NAME,
                   COALESCE(F.RDB$CHARACTER_LENGTH, F.RDB$FIELD_LENGTH) AS MAX_LEN
            FROM RDB$RELATION_FIELDS RF
            JOIN RDB$FIELDS F ON RF.RDB$FIELD_SOURCE = F.RDB$FIELD_NAME
            WHERE RF.RDB$RELATION_NAME = @T
              AND F.RDB$FIELD_TYPE IN (14, 37)
            """;

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@T", FbDbType.Char).Value = tableName.Trim().ToUpperInvariant();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var col = reader.GetString(0)?.Trim();
            if (string.IsNullOrEmpty(col)) continue;
            try
            {
                result[col] = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
            catch
            {
                // ignore
            }
        }

        return result;
    }

    public static Dictionary<string, object?> FitStrings(
        Dictionary<string, object?> row,
        Dictionary<string, int> stringLengths)
    {
        var fitted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in row)
        {
            if (value is string s && stringLengths.TryGetValue(key.ToUpperInvariant(), out var maxLen)
                && maxLen > 0 && s.Length > maxLen)
            {
                fitted[key] = s[..maxLen];
            }
            else
            {
                fitted[key] = value;
            }
        }

        return fitted;
    }

    public static async Task<long> NextKeyAsync(
        FbConnection conn,
        string tableName,
        string keyColumn,
        IReadOnlyList<string> generatorCandidates,
        CancellationToken cancellationToken = default,
        FbTransaction? transaction = null)
    {
        await using var maxCmd = new FbCommand($"SELECT COALESCE(MAX({keyColumn}), 0) FROM {tableName}", conn, transaction);
        var maxObj = await maxCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var maxExisting = maxObj is null or DBNull ? 0L : Convert.ToInt64(maxObj, CultureInfo.InvariantCulture);

        var candidate = maxExisting + 1;

        foreach (var gen in generatorCandidates)
        {
            if (string.IsNullOrWhiteSpace(gen)) continue;
            try
            {
                var safe = gen.Trim().ToUpperInvariant();
                await using var genCmd = new FbCommand($"SELECT GEN_ID({safe}, 1) FROM RDB$DATABASE", conn, transaction);
                var genObj = await genCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (genObj is null or DBNull) continue;
                var genVal = Convert.ToInt64(genObj, CultureInfo.InvariantCulture);
                candidate = genVal > maxExisting ? genVal : maxExisting + 1;
                break;
            }
            catch
            {
                // try next generator
            }
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!await KeyExistsAsync(conn, tableName, keyColumn, candidate, transaction, cancellationToken).ConfigureAwait(false))
                return candidate;
            candidate++;
        }

        throw new InvalidOperationException($"Could not allocate a free {keyColumn} for {tableName}.");
    }

    private static async Task<bool> KeyExistsAsync(
        FbConnection conn,
        string tableName,
        string keyColumn,
        long candidate,
        FbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT FIRST 1 1 FROM {tableName} WHERE {keyColumn} = @K";
        await using var cmd = new FbCommand(sql, conn, transaction);
        cmd.Parameters.Add("@K", FbDbType.BigInt).Value = candidate;
        var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return obj is not null and not DBNull;
    }

    private static readonly string[] GlobalDtlKeyTables =
    [
        "PH_GRDTL", "PH_PODTL", "PH_PQDTL", "SL_SODTL", "SL_IVDTL", "PH_CNDTL", "PH_DNDTL",
        "SL_QTDTL", "PH_IVDTL", "PH_PDDTL", "ST_ITEM_TPLDTL"
    ];

    /// <summary>SQL Accounting often shares <c>DTLKEY</c> across document detail tables.</summary>
    public static async Task<long> NextGlobalDtlKeyAsync(
        FbConnection conn,
        string detailKeyColumn,
        CancellationToken cancellationToken = default,
        FbTransaction? transaction = null)
    {
        long maxExisting = 0;
        foreach (var table in GlobalDtlKeyTables)
        {
            try
            {
                await using var maxCmd = new FbCommand(
                    $"SELECT COALESCE(MAX({detailKeyColumn}), 0) FROM {table}",
                    conn,
                    transaction);
                var maxObj = await maxCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (maxObj is null or DBNull) continue;
                var v = Convert.ToInt64(maxObj, CultureInfo.InvariantCulture);
                if (v > maxExisting) maxExisting = v;
            }
            catch
            {
                // table may not exist on this tenant
            }
        }

        var candidate = maxExisting + 1;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (!await GlobalDtlKeyExistsAsync(conn, detailKeyColumn, candidate, transaction, cancellationToken).ConfigureAwait(false))
                return candidate;
            candidate++;
        }

        throw new InvalidOperationException($"Could not allocate a free global {detailKeyColumn}.");
    }

    public static async Task<bool> GlobalDtlKeyExistsAsync(
        FbConnection conn,
        string detailKeyColumn,
        long candidate,
        FbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        foreach (var table in GlobalDtlKeyTables)
        {
            try
            {
                if (await KeyExistsAsync(conn, table, detailKeyColumn, candidate, transaction, cancellationToken).ConfigureAwait(false))
                    return true;
            }
            catch
            {
                // ignore missing tables
            }
        }

        return false;
    }

    /// <summary>Allocate a line key unique for the given header <c>DOCKEY</c> (composite PK on many SQL doc detail tables).</summary>
    public static async Task<long> NextDetailKeyForHeaderAsync(
        FbConnection conn,
        string tableName,
        string headerFkColumn,
        long headerDocKey,
        string detailKeyColumn,
        CancellationToken cancellationToken = default,
        FbTransaction? transaction = null)
    {
        await using var maxCmd = new FbCommand(
            $"SELECT COALESCE(MAX({detailKeyColumn}), 0) FROM {tableName} WHERE {headerFkColumn} = @H",
            conn,
            transaction);
        maxCmd.Parameters.Add("@H", FbDbType.BigInt).Value = headerDocKey;
        var maxObj = await maxCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var candidate = (maxObj is null or DBNull ? 0L : Convert.ToInt64(maxObj, CultureInfo.InvariantCulture)) + 1;

        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var chk = new FbCommand(
                $"SELECT FIRST 1 1 FROM {tableName} WHERE {headerFkColumn} = @H AND {detailKeyColumn} = @D",
                conn,
                transaction);
            chk.Parameters.Add("@H", FbDbType.BigInt).Value = headerDocKey;
            chk.Parameters.Add("@D", FbDbType.BigInt).Value = candidate;
            var hit = await chk.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (hit is null or DBNull)
                return candidate;
            candidate++;
        }

        throw new InvalidOperationException($"Could not allocate a free {detailKeyColumn} for {tableName} (header {headerDocKey}).");
    }

    public static async Task InsertDynamicAsync(
        FbConnection conn,
        string tableName,
        Dictionary<string, object?> data,
        HashSet<string> existingColumns,
        CancellationToken cancellationToken = default,
        FbTransaction? transaction = null)
    {
        var filtered = new List<(string Col, object? Val)>();
        foreach (var (col, val) in data)
        {
            var name = col.Trim().ToUpperInvariant();
            if (existingColumns.Contains(name))
                filtered.Add((name, val));
        }

        if (filtered.Count == 0)
            throw new InvalidOperationException($"No matching columns found for insert into {tableName}.");

        var cols = string.Join(", ", filtered.Select(f => f.Col));
        var placeholders = string.Join(", ", filtered.Select(_ => "?"));
        var sql = $"INSERT INTO {tableName} ({cols}) VALUES ({placeholders})";

        await using var cmd = new FbCommand(sql, conn, transaction);
        foreach (var (_, val) in filtered)
            cmd.Parameters.Add(new FbParameter { Value = val ?? DBNull.Value });

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
