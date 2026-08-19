using System.Globalization;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Helpers;

internal static class DocumentHeaderAmount
{
    public static async Task<decimal?> ReadAsync(
        FbConnection conn,
        string tableName,
        int docKey,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0 || string.IsNullOrWhiteSpace(tableName))
            return null;

        await using var cmd = new FbCommand(
            $"SELECT FIRST 1 COALESCE(DOCAMT, 0) AS AMOUNT FROM {tableName} WHERE DOCKEY = @D",
            conn);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
            return null;
        return Convert.ToDecimal(result, CultureInfo.InvariantCulture);
    }
}
