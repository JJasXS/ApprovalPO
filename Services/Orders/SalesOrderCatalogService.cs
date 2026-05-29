using System.Data.Common;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.Orders;

/// <summary>
/// Loads sales orders from Firebird <c>SL_SO</c> for the configured tenant.
/// <c>SL_SO.UDF_SOSTATUS</c> (<c>PENDING</c> / <c>APPROVED</c> / <c>CANCELLED</c> / <c>REJECTED</c>) drives tabs.
/// </summary>
public sealed class SalesOrderCatalogService : ISalesOrderCatalog
{
    internal const string DefaultSalesOrdersSql = """
        SELECT FIRST 200
          DOCKEY,
          TRIM(DOCNO) AS SONUMBER,
          TRIM(COALESCE(COMPANYNAME, CODE, '')) AS CUSTOMER,
          COALESCE(DOCAMT, 0) AS AMOUNT,
          TRIM(COALESCE(CAST(DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
          COALESCE(DOCDATE, CURRENT_DATE) AS ORDERDATE,
          CAST(CASE UPPER(TRIM(COALESCE(CAST(UDF_SOSTATUS AS VARCHAR(40)), '')))
            WHEN 'APPROVED' THEN 1
            WHEN 'CANCELLED' THEN 0
            WHEN 'REJECTED' THEN 0
            ELSE NULL
          END AS SMALLINT) AS TRANSFERABLEINT,
          CAST(CASE UPPER(TRIM(COALESCE(CAST(UDF_SOSTATUS AS VARCHAR(40)), '')))
            WHEN 'APPROVED' THEN 'Approved'
            WHEN 'CANCELLED' THEN 'Cancelled'
            WHEN 'REJECTED' THEN 'Rejected'
            ELSE 'Pending'
          END AS VARCHAR(20)) AS SOSTATUS
        FROM SL_SO
        ORDER BY DOCDATE DESC NULLS LAST, DOCNO DESC
        """;

    internal const string DefaultSalesOrderLinesSql = """
        SELECT
          COALESCE(D.SEQ, 0) AS LINENO,
          TRIM(COALESCE(D.ITEMCODE, '')) AS ITEMCODE,
          TRIM(COALESCE(CAST(D.DESCRIPTION AS VARCHAR(800)), '')) AS DESCRIPTION,
          COALESCE(D.SQTY, 0) AS SQTY,
          COALESCE(D.SUOMQTY, 0) AS SUOMQTY,
          COALESCE(D.QTY, 0) AS QTY,
          COALESCE(D.UNITPRICE, 0) AS UNITPRICE,
          COALESCE(D.AMOUNT, 0) AS LINEAMOUNT,
          CAST(CASE
            WHEN D.TRANSFERABLE IS NULL THEN NULL
            WHEN D.TRANSFERABLE IS TRUE THEN 1
            WHEN D.TRANSFERABLE IS FALSE THEN 0
            ELSE NULL
          END AS SMALLINT) AS TRANSFERABLEINT
        FROM SL_SODTL D
        WHERE D.DOCKEY = @DocKey
        ORDER BY COALESCE(D.SEQ, 0)
        """;

    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;

    public SalesOrderCatalogService(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<SalesOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            throw new InvalidOperationException("TenantBootstrap:TenantCode is required to load SL_SO.");

        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new FbCommand(DefaultSalesOrdersSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<SalesOrderRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapRow(reader));

        return list;
    }

    public async Task<(bool Success, string? ErrorMessage)> TrySetHeaderListStatusAsync(
        int docKey,
        string listStatus,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return (false, "Invalid document key.");

        var statusText = (listStatus ?? "").Trim() switch
        {
            var s when s.Equals("Pending",   StringComparison.OrdinalIgnoreCase) => "PENDING",
            var s when s.Equals("Approved",  StringComparison.OrdinalIgnoreCase) => "APPROVED",
            var s when s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) => "CANCELLED",
            var s when s.Equals("Rejected",  StringComparison.OrdinalIgnoreCase) => "REJECTED",
            _ => null,
        };
        if (statusText is null)
            return (false, "ListStatus must be Pending, Approved, Cancelled, or Rejected.");

        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            return (false, "TenantBootstrap:TenantCode is required.");

        try
        {
            var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

            await using var conn = new FbConnection(connStr);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new FbCommand(
                "UPDATE SL_SO SET UDF_SOSTATUS = @S WHERE DOCKEY = @D",
                conn);

            cmd.Parameters.Add("@S", FbDbType.VarChar, 20).Value = statusText;
            cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;

            var n = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return (false, "No row was updated (DOCKEY not found or UDF_SOSTATUS column mismatch).");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> TrySetLineTransferableAsync(
        int docKey,
        int lineNo,
        bool transferable,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return (false, "Invalid document key.");
        if (lineNo < 0)
            return (false, "Invalid line number.");

        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            return (false, "TenantBootstrap:TenantCode is required.");

        try
        {
            var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

            await using var conn = new FbConnection(connStr);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = """
                UPDATE SL_SODTL D
                SET D.TRANSFERABLE = @T
                WHERE D.DOCKEY = @D
                  AND COALESCE(D.SEQ, 0) = @L
                  AND EXISTS (
                    SELECT 1 FROM SL_SO P
                    WHERE P.DOCKEY = @D
                      AND (P.UDF_SOSTATUS IS NULL
                           OR TRIM(COALESCE(CAST(P.UDF_SOSTATUS AS VARCHAR(40)), '')) = ''
                           OR UPPER(TRIM(CAST(P.UDF_SOSTATUS AS VARCHAR(40)))) = 'PENDING')
                  )
                """;

            await using var cmd = new FbCommand(sql, conn);
            cmd.Parameters.Add("@T", FbDbType.Boolean).Value = transferable;
            cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
            cmd.Parameters.Add("@L", FbDbType.Integer).Value = lineNo;

            var n = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return (false, "No line updated (check DOCKEY/SEQ, or header is not pending).");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<SalesOrderLineRow>> GetSalesOrderLinesAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return Array.Empty<SalesOrderLineRow>();

        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            throw new InvalidOperationException("TenantBootstrap:TenantCode is required to load SL_SODTL.");

        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new FbCommand(DefaultSalesOrderLinesSql, conn);
        cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<SalesOrderLineRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapLineRow(reader));

        return list;
    }

    internal static string NormalizeTabStatus(string? rawFromReader, bool? transferable)
    {
        if (!string.IsNullOrWhiteSpace(rawFromReader))
        {
            if (string.Equals(rawFromReader, "Pending",   StringComparison.OrdinalIgnoreCase)) return "Pending";
            if (string.Equals(rawFromReader, "Approved",  StringComparison.OrdinalIgnoreCase)) return "Approved";
            if (string.Equals(rawFromReader, "Rejected",  StringComparison.OrdinalIgnoreCase)) return "Rejected";
            if (string.Equals(rawFromReader, "REJECTED",  StringComparison.OrdinalIgnoreCase)) return "Rejected";
            if (string.Equals(rawFromReader, "Cancelled", StringComparison.OrdinalIgnoreCase)
             || string.Equals(rawFromReader, "Canceled",  StringComparison.OrdinalIgnoreCase)
             || string.Equals(rawFromReader, "CANCELLED", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
        }

        return transferable switch { null => "Pending", true => "Approved", false => "Cancelled" };
    }

    private static SalesOrderRow MapRow(DbDataReader reader)
    {
        var so       = GetString(reader, "SONUMBER", "DOCNO") ?? "";
        var customer = GetString(reader, "CUSTOMER", "COMPANYNAME") ?? "";
        var amount   = GetDecimal(reader, "AMOUNT", "DOCAMT");
        var desc     = GetString(reader, "DESCRIPTION") ?? "";
        var date     = GetDateTime(reader, "ORDERDATE", "DOCDATE") ?? DateTime.UtcNow.Date;
        var transfer = GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE");
        var rawStatus = (GetString(reader, "SOSTATUS", "UDF_SOSTATUS", "STATUS") ?? "").Trim();
        var status   = NormalizeTabStatus(rawStatus, transfer);

        return new SalesOrderRow
        {
            DocKey      = GetInt32(reader, "DOCKEY", "SOKEY", "ID"),
            SoNumber    = so.Trim(),
            Customer    = customer.Trim(),
            Amount      = amount,
            Status      = status,
            Description = desc.Trim(),
            OrderDate   = date,
            Transferable = transfer,
        };
    }

    private static SalesOrderLineRow MapLineRow(DbDataReader reader) =>
        new SalesOrderLineRow
        {
            LineNo      = GetInt32(reader, "LINENO", "SEQ"),
            ItemCode    = (GetString(reader, "ITEMCODE") ?? "").Trim(),
            Description = (GetString(reader, "DESCRIPTION") ?? "").Trim(),
            Sqty        = GetDecimal(reader, "SQTY"),
            SuomQty     = GetDecimal(reader, "SUOMQTY"),
            Qty         = GetDecimal(reader, "QTY", "QUANTITY"),
            UnitPrice   = GetDecimal(reader, "UNITPRICE", "RATE"),
            LineAmount  = GetDecimal(reader, "LINEAMOUNT", "AMOUNT", "TOTAL"),
            Transferable = GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE"),
        };

    private static int GetInt32(DbDataReader r, params string[] cols)
    {
        foreach (var c in cols)
        {
            var o = Ord(r, c); if (o < 0) continue;
            if (r.IsDBNull(o)) return 0;
            var v = r.GetValue(o);
            if (v is int i)  return i;
            if (v is long l) return l > int.MaxValue ? int.MaxValue : (int)l;
            if (v is short s) return s;
            if (int.TryParse(v?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) return n;
        }
        return 0;
    }

    private static string? GetString(DbDataReader r, params string[] cols)
    {
        foreach (var c in cols) { var o = Ord(r, c); if (o >= 0 && !r.IsDBNull(o)) return r.GetValue(o)?.ToString(); }
        return null;
    }

    private static decimal GetDecimal(DbDataReader r, params string[] cols)
    {
        foreach (var c in cols)
        {
            var o = Ord(r, c); if (o < 0) continue;
            if (r.IsDBNull(o)) return 0m;
            var v = r.GetValue(o);
            if (v is decimal d) return d;
            return Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        return 0m;
    }

    private static DateTime? GetDateTime(DbDataReader r, params string[] cols)
    {
        foreach (var c in cols)
        {
            var o = Ord(r, c); if (o < 0) continue;
            if (r.IsDBNull(o)) return null;
            var v = r.GetValue(o);
            if (v is DateTime dt) return dt.Date;
            if (DateTime.TryParse(v?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var p)) return p.Date;
        }
        return null;
    }

    private static bool? GetBoolNullable(DbDataReader r, params string[] cols)
    {
        foreach (var c in cols)
        {
            var o = Ord(r, c); if (o < 0) continue;
            if (r.IsDBNull(o)) return null;
            var v = r.GetValue(o);
            if (v is bool b)   return b;
            if (v is short s)  return s != 0;
            if (v is int i)    return i != 0;
            if (v is string str)
            {
                var u = str.Trim().ToUpperInvariant();
                if (u is "T" or "Y" or "1" or "TRUE" or "YES")  return true;
                if (u is "F" or "N" or "0" or "FALSE" or "NO")  return false;
            }
            if (int.TryParse(v?.ToString(), out var n)) return n != 0;
        }
        return null;
    }

    private static int Ord(DbDataReader r, string col)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
