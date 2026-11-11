using System.Globalization;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

/// <summary>Shared Firebird row load/coerce helpers for document transfers.</summary>
public static class FirebirdRowHelpers
{
    public static async Task<Dictionary<string, object?>?> LoadHeaderAsync(
        FbConnection conn,
        FbTransaction tx,
        string table,
        string keyCol,
        int docKey,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT FIRST 1 * FROM {table} WHERE {keyCol} = @K";
        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@K", FbDbType.Integer).Value = docKey;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadRow(reader);
    }

    public static async Task<List<Dictionary<string, object?>>> LoadDetailsAsync(
        FbConnection conn,
        FbTransaction tx,
        string table,
        string fkCol,
        int docKey,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT * FROM {table} WHERE {fkCol} = @K ORDER BY COALESCE(SEQ, 0)";
        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@K", FbDbType.Integer).Value = docKey;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(ReadRow(reader));
        return list;
    }

    public static Dictionary<string, object?> ReadRow(FbDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return row;
    }

    public static int GetInt(IReadOnlyDictionary<string, object?> row, string col)
    {
        if (!row.TryGetValue(col, out var v) || v is null) return 0;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    public static decimal ToDecimal(object? v, decimal fallback = 0m)
    {
        if (v is null or DBNull) return fallback;
        try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    public static decimal Money(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    public static bool CoerceBool(object? v)
    {
        if (v is true or 1) return true;
        var t = (v?.ToString() ?? "").Trim().ToLowerInvariant();
        return t is "true" or "1" or "t" or "y" or "yes";
    }

    public static string? Clean(object? v)
    {
        var s = v?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public static DateTime? NormalizeDate(object? v)
    {
        if (v is null or DBNull) return null;
        if (v is DateTime dt) return dt.Date;
        if (DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var p))
            return p.Date;
        return null;
    }

    /// <summary>Stock qty for scan/transfer: <c>SQTY</c>, then <c>QTY</c>, then <c>SUOMQTY</c>.</summary>
    public static decimal LineSourceQty(IReadOnlyDictionary<string, object?> line)
    {
        var sqty = Money(ToDecimal(line.GetValueOrDefault("SQTY")));
        if (sqty > 0) return sqty;
        var qty = Money(ToDecimal(line.GetValueOrDefault("QTY")));
        if (qty > 0) return qty;
        return Money(ToDecimal(line.GetValueOrDefault("SUOMQTY")));
    }

    public static bool IsTransferable(IReadOnlyDictionary<string, object?> line)
    {
        if (!line.TryGetValue("TRANSFERABLE", out var v) || v is null)
            return true;
        return CoerceBool(v);
    }
}
