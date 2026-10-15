using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Tests.ApprovalDbSmoke;

internal sealed class SoSnapshot
{
    public int DocKey { get; init; }
    public string Status { get; init; } = "";
    public string StatusColumn { get; init; } = "UDF_SOSTATUS";
    public int? LineNo { get; init; }
    public bool? LineTransferable { get; init; }

    public static async Task<SoSnapshot?> ReadAsync(SmokeHost host, int docKey, string statusColumn)
    {
        var sql = $"SELECT TRIM(COALESCE(CAST({statusColumn} AS VARCHAR(40)), '')) FROM SL_SO WHERE DOCKEY = @D";
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "snapshot SL_SO").ConfigureAwait(false);
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
        var status = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (status is null)
            return null;
        var line = await ReadLineAsync(conn, docKey).ConfigureAwait(false);
        return new SoSnapshot
        {
            DocKey = docKey,
            StatusColumn = statusColumn,
            Status = status is DBNull ? "" : status.ToString()?.Trim() ?? "",
            LineNo = line.LineNo,
            LineTransferable = line.Transferable
        };
    }

    public async Task RestoreAsync(SmokeHost host)
    {
        var sql = $"UPDATE SL_SO SET {StatusColumn} = @S WHERE DOCKEY = @D";
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "restore SL_SO").ConfigureAwait(false);
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@S", FbDbType.VarChar).Value = Status.Length == 0 ? DBNull.Value : Status;
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = DocKey;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (LineNo is null) return;

        await using var line = new FbCommand(
            "UPDATE SL_SODTL SET TRANSFERABLE = @T WHERE DOCKEY = @D AND COALESCE(SEQ, 0) = @L",
            conn);
        line.Parameters.Add("@T", FbDbType.Boolean).Value = (object?)LineTransferable ?? DBNull.Value;
        line.Parameters.Add("@D", FbDbType.Integer).Value = DocKey;
        line.Parameters.Add("@L", FbDbType.Integer).Value = LineNo.Value;
        await line.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<(int? LineNo, bool? Transferable)> ReadLineAsync(FbConnection conn, int docKey)
    {
        await using var cmd = new FbCommand(
            "SELECT FIRST 1 COALESCE(SEQ, 0), TRANSFERABLE FROM SL_SODTL WHERE DOCKEY = @D ORDER BY 1",
            conn);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            return (null, null);
        return (reader.GetInt32(0), ReadFlag(reader, 1));
    }

    private static bool? ReadFlag(FbDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        var value = reader.GetValue(index);
        if (value is bool flag) return flag;
        return Convert.ToInt32(value) != 0;
    }
}
