using System.Collections.Concurrent;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;

namespace ApprovalPO.Helpers;

/// <summary>Reads Firebird <c>RDB$RELATION_FIELDS</c> (cached per database + table).</summary>
public static class FirebirdSchemaHelper
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> ColumnCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<HashSet<string>> GetColumnNamesAsync(
        FbConnection conn,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var table = (tableName ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(table))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cacheKey = CacheKey(conn, table);
        if (ColumnCache.TryGetValue(cacheKey, out var cached))
            return cached;

        const string sql = """
            SELECT TRIM(rf.RDB$FIELD_NAME) AS COL_NAME
            FROM RDB$RELATION_FIELDS rf
            WHERE rf.RDB$RELATION_NAME = @T
            ORDER BY rf.RDB$FIELD_POSITION
            """;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@T", FbDbType.Char).Value = table;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.IsDBNull(0) ? null : reader.GetString(0)?.Trim();
            if (!string.IsNullOrEmpty(name))
                set.Add(name);
        }

        ColumnCache[cacheKey] = set;
        return set;
    }

    public static void InvalidateColumnCache(string tableName)
    {
        var table = (tableName ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(table))
            return;

        var suffix = "|" + table;
        foreach (var key in ColumnCache.Keys)
        {
            if (key.Equals(table, StringComparison.OrdinalIgnoreCase)
                || key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                ColumnCache.TryRemove(key, out _);
        }
    }

    /// <summary>Drop all cached schemas (e.g. after switching active company database).</summary>
    public static void ClearColumnCache() => ColumnCache.Clear();

    private static string CacheKey(FbConnection conn, string table)
    {
        var database = (conn.Database ?? "").Trim();
        if (string.IsNullOrEmpty(database))
            database = (conn.DataSource ?? "").Trim();
        if (string.IsNullOrEmpty(database))
            database = "_";
        return database + "|" + table;
    }

    /// <summary>Returns the first candidate that exists on the table (case-insensitive).</summary>
    public static string? PickColumn(HashSet<string> columns, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (columns.Contains(c.Trim()))
                return c.Trim();
        }

        return null;
    }

    /// <summary><c>ST_XTRANS</c> (or similar) columns needed to link PO header to another document.</summary>
    public static bool HasDocumentLinkColumns(HashSet<string>? columns) =>
        columns is not null
        && columns.Contains("FROMDOCKEY")
        && columns.Contains("TODOCKEY")
        && columns.Contains("FROMDOCTYPE")
        && columns.Contains("TODOCTYPE");

    public static async Task<bool> RelationExistsAsync(
        FbConnection conn,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new FbCommand(
            """
            SELECT 1
            FROM RDB$RELATIONS
            WHERE RDB$RELATION_NAME = @T
              AND COALESCE(RDB$SYSTEM_FLAG, 0) = 0
            """,
            conn);
        cmd.Parameters.Add("@T", FbDbType.Char).Value = tableName.Trim().ToUpperInvariant();
        var row = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return row is not null && row != DBNull.Value;
    }

    /// <summary>Adds a VARCHAR column when missing. Optional backfill of blank/null rows.</summary>
    public static async Task<bool> EnsureVarcharColumnAsync(
        FbConnection conn,
        string tableName,
        string columnName,
        int length,
        ILogger? logger = null,
        string? backfillValue = null,
        CancellationToken cancellationToken = default)
    {
        var table = tableName.Trim().ToUpperInvariant();
        var column = columnName.Trim().ToUpperInvariant();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!await RelationExistsAsync(conn, table, cancellationToken).ConfigureAwait(false))
        {
            logger?.LogWarning("{Table} not found; {Column} ensure skipped.", table, column);
            return false;
        }

        var cols = await GetColumnNamesAsync(conn, table, cancellationToken).ConfigureAwait(false);
        if (cols.Contains(column))
            return true;

        try
        {
            await using var add = new FbCommand($"ALTER TABLE {table} ADD {column} VARCHAR({length})", conn);
            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Added {Table}.{Column} VARCHAR({Length}).", table, column, length);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                InvalidateColumnCache(table);
                return true;
            }

            logger?.LogWarning(ex, "Could not add {Table}.{Column}.", table, column);
            return false;
        }

        InvalidateColumnCache(table);
        if (string.IsNullOrEmpty(backfillValue))
            return true;

        try
        {
            await using var backfill = new FbCommand(
                $"""
                UPDATE {table}
                SET {column} = @V
                WHERE {column} IS NULL
                   OR TRIM(COALESCE({column}, '')) = ''
                """,
                conn);
            backfill.Parameters.Add("@V", FbDbType.VarChar, length).Value = backfillValue;
            await backfill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "{Table}.{Column} backfill skipped.", table, column);
        }

        return true;
    }
}
