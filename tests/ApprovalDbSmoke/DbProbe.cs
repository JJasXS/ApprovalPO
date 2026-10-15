using ApprovalPO.Helpers;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Tests.ApprovalDbSmoke;

internal static class DbProbe
{
    public static async Task<HashSet<string>> ColumnsAsync(SmokeHost host, string table)
    {
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "schema " + table).ConfigureAwait(false);
        return await FirebirdSchemaHelper.GetColumnNamesAsync(conn, table).ConfigureAwait(false);
    }

    public static async Task<int> CountAsync(SmokeHost host, string table)
    {
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "count " + table).ConfigureAwait(false);
        await using var cmd = new FbCommand($"SELECT COUNT(*) FROM {table}", conn);
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    public static async Task<List<string>> DistinctTextAsync(SmokeHost host, string table, string column)
    {
        var sql = $"""
            SELECT DISTINCT UPPER(TRIM(COALESCE(CAST({column} AS VARCHAR(40)), ''))) AS V
            FROM {table}
            """;
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "distinct " + column).ConfigureAwait(false);
        await using var cmd = new FbCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var values = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
            values.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        return values;
    }

    public static async Task<string> HeaderTextAsync(SmokeHost host, string table, string column, int docKey)
    {
        var sql = $"SELECT TRIM(COALESCE(CAST({column} AS VARCHAR(40)), '')) FROM {table} WHERE DOCKEY = @D";
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "read " + column).ConfigureAwait(false);
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null || value is DBNull ? "" : value.ToString()?.Trim() ?? "";
    }
}
