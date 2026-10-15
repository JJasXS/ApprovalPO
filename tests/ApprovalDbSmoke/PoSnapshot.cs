using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Tests.ApprovalDbSmoke;

internal sealed class PoSnapshot
{
    public int DocKey { get; init; }
    public string Status { get; init; } = "";
    public string Level1 { get; init; } = "";
    public DateTime? Level1At { get; init; }
    public string Level2 { get; init; } = "";
    public DateTime? Level2At { get; init; }
    public string Level3 { get; init; } = "";
    public DateTime? Level3At { get; init; }
    public int? LineNo { get; init; }
    public bool? LineTransferable { get; init; }
    public string StatusColumn { get; init; } = "UDF_POSTATUS";

    public static async Task<PoSnapshot?> ReadAsync(SmokeHost host, int docKey, string statusColumn)
    {
        var sql = $"""
            SELECT
              TRIM(COALESCE(CAST({statusColumn} AS VARCHAR(40)), '')) AS STATUS,
              TRIM(COALESCE(CAST(UDF_POAPPL1 AS VARCHAR(80)), '')) AS L1,
              UDF_POAPPL1DT AS L1DT,
              TRIM(COALESCE(CAST(UDF_POAPPL2 AS VARCHAR(80)), '')) AS L2,
              UDF_POAPPL2DT AS L2DT,
              TRIM(COALESCE(CAST(UDF_POAPPL3 AS VARCHAR(80)), '')) AS L3,
              UDF_POAPPL3DT AS L3DT
            FROM PH_PO
            WHERE DOCKEY = @D
            """;
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "snapshot PH_PO").ConfigureAwait(false);
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            return null;

        var line = await ReadLineAsync(conn, docKey).ConfigureAwait(false);
        return new PoSnapshot
        {
            DocKey = docKey,
            StatusColumn = statusColumn,
            Status = Text(reader, 0),
            Level1 = Text(reader, 1),
            Level1At = When(reader, 2),
            Level2 = Text(reader, 3),
            Level2At = When(reader, 4),
            Level3 = Text(reader, 5),
            Level3At = When(reader, 6),
            LineNo = line.LineNo,
            LineTransferable = line.Transferable
        };
    }

    public async Task RestoreAsync(SmokeHost host)
    {
        var sql = $"""
            UPDATE PH_PO
            SET {StatusColumn} = @S,
                UDF_POAPPL1 = @E1, UDF_POAPPL1DT = @T1,
                UDF_POAPPL2 = @E2, UDF_POAPPL2DT = @T2,
                UDF_POAPPL3 = @E3, UDF_POAPPL3DT = @T3
            WHERE DOCKEY = @D
            """;
        await using var conn = await host.Resolver.OpenConnectionAsync(host.Config, "restore PH_PO").ConfigureAwait(false);
        await using var cmd = new FbCommand(sql, conn);
        AddText(cmd, "@S", Status);
        AddText(cmd, "@E1", Level1);
        AddTime(cmd, "@T1", Level1At);
        AddText(cmd, "@E2", Level2);
        AddTime(cmd, "@T2", Level2At);
        AddText(cmd, "@E3", Level3);
        AddTime(cmd, "@T3", Level3At);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = DocKey;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (LineNo is not null)
            await RestoreLineAsync(conn).ConfigureAwait(false);
    }

    private async Task RestoreLineAsync(FbConnection conn)
    {
        await using var cmd = new FbCommand(
            "UPDATE PH_PODTL SET TRANSFERABLE = @T WHERE DOCKEY = @D AND COALESCE(SEQ, 0) = @L",
            conn);
        cmd.Parameters.Add("@T", FbDbType.Boolean).Value = (object?)LineTransferable ?? DBNull.Value;
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = DocKey;
        cmd.Parameters.Add("@L", FbDbType.Integer).Value = LineNo!.Value;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<(int? LineNo, bool? Transferable)> ReadLineAsync(FbConnection conn, int docKey)
    {
        await using var cmd = new FbCommand(
            "SELECT FIRST 1 COALESCE(SEQ, 0), TRANSFERABLE FROM PH_PODTL WHERE DOCKEY = @D ORDER BY 1",
            conn);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            return (null, null);
        return (reader.GetInt32(0), ReadBool(reader, 1));
    }

    private static void AddText(FbCommand cmd, string name, string value) =>
        cmd.Parameters.Add(name, FbDbType.VarChar).Value = value.Length == 0 ? DBNull.Value : value;

    private static void AddTime(FbCommand cmd, string name, DateTime? value) =>
        cmd.Parameters.Add(name, FbDbType.TimeStamp).Value = (object?)value ?? DBNull.Value;

    private static string Text(FbDataReader reader, int index) =>
        reader.IsDBNull(index) ? "" : reader.GetString(index).Trim();

    private static DateTime? When(FbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetDateTime(index);

    private static bool? ReadBool(FbDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        var value = reader.GetValue(index);
        if (value is bool flag) return flag;
        return Convert.ToInt32(value) != 0;
    }
}
