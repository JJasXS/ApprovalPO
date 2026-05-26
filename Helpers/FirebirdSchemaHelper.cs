using System.Collections.Concurrent;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Reads Firebird <c>RDB$RELATION_FIELDS</c> (cached per table).</summary>
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

        if (ColumnCache.TryGetValue(table, out var cached))
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

        ColumnCache[table] = set;
        return set;
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
}
