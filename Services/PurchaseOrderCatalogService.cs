using System.Data.Common;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Options;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services;

/// <summary>
/// Loads purchase orders from Firebird <c>PH_PO</c> for the configured tenant.
/// Default: <c>PH_PO.UDF_POSTATUS</c> (<c>PENDING</c> / <c>APPROVED</c> / <c>CANCELLED</c> / <c>REJECTED</c>) drives tabs; <c>TRANSFERABLEINT</c> is derived for JSON compatibility.
/// </summary>
public sealed class PurchaseOrderCatalogService : IPurchaseOrderCatalog
{
    /// <summary>
    /// Default reads <c>PH_PO.UDF_POSTATUS</c> (VARCHAR). Maps to <c>PQSTATUS</c> + <c>TRANSFERABLEINT</c> for the UI/API.
    /// Override with <see cref="ApprovalOptions.PurchaseOrdersSql"/> if your view uses different column names.
    /// Include <c>DOCKEY</c> so line items can load.
    /// </summary>
    internal const string DefaultPurchaseOrdersSql = """
        SELECT FIRST 200
          DOCKEY,
          TRIM(DOCNO) AS PONUMBER,
          TRIM(COALESCE(COMPANYNAME, CODE, '')) AS VENDOR,
          COALESCE(DOCAMT, 0) AS AMOUNT,
          TRIM(COALESCE(CAST(DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
          COALESCE(DOCDATE, CURRENT_DATE) AS ORDERDATE,
          CAST(CASE UPPER(TRIM(COALESCE(CAST(UDF_POSTATUS AS VARCHAR(40)), '')))
            WHEN 'APPROVED' THEN 1
            WHEN 'CANCELLED' THEN 0
            WHEN 'REJECTED' THEN 0
            ELSE NULL
          END AS SMALLINT) AS TRANSFERABLEINT,
          CAST(CASE UPPER(TRIM(COALESCE(CAST(UDF_POSTATUS AS VARCHAR(40)), '')))
            WHEN 'APPROVED' THEN 'Approved'
            WHEN 'CANCELLED' THEN 'Cancelled'
            WHEN 'REJECTED' THEN 'Rejected'
            ELSE 'Pending'
          END AS VARCHAR(20)) AS PQSTATUS
        FROM PH_PO
        ORDER BY DOCDATE DESC NULLS LAST, DOCNO DESC
        """;

    /// <summary>Detail lines for <c>PH_PODTL</c>; parameter <c>@DocKey</c>. Includes <c>SQTY</c>, <c>SUOMQTY</c>, and <c>TRANSFERABLEINT</c> from <c>D.TRANSFERABLE</c>.</summary>
    internal const string DefaultPurchaseRequestLinesSql = """
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
        FROM PH_PODTL D
        WHERE D.DOCKEY = @DocKey
        ORDER BY COALESCE(D.SEQ, 0)
        """;

    private readonly IOptions<ApprovalOptions> _approval;
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;

    public PurchaseOrderCatalogService(
        IOptions<ApprovalOptions> approval,
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration)
    {
        _approval = approval;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            throw new InvalidOperationException("TenantBootstrap:TenantCode is required to load PH_PO.");

        var sql = string.IsNullOrWhiteSpace(_approval.Value.PurchaseOrdersSql)
            ? DefaultPurchaseOrdersSql
            : _approval.Value.PurchaseOrdersSql!;

        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new FbCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<PurchaseOrderRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapRow(reader));

        return list;
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? ErrorMessage)> TrySetHeaderListStatusAsync(
        int docKey,
        string listStatus,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return (false, "Invalid document key.");

        var statusText = (listStatus ?? "").Trim() switch
        {
            var s when s.Equals("Pending", StringComparison.OrdinalIgnoreCase) => "PENDING",
            var s when s.Equals("Approved", StringComparison.OrdinalIgnoreCase) => "APPROVED",
            var s when s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) => "CANCELLED",
            var s when s.Equals("Rejected", StringComparison.OrdinalIgnoreCase) => "REJECTED",
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
                "UPDATE PH_PO SET UDF_POSTATUS = @S WHERE DOCKEY = @D",
                conn);

            cmd.Parameters.Add("@S", FbDbType.VarChar, 20).Value = statusText;
            cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;

            var n = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return (false, "No row was updated (DOCKEY not found or UDF_POSTATUS column mismatch).");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <inheritdoc />
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
                UPDATE PH_PODTL D
                SET D.TRANSFERABLE = @T
                WHERE D.DOCKEY = @D
                  AND COALESCE(D.SEQ, 0) = @L
                  AND EXISTS (
                    SELECT 1 FROM PH_PO P
                    WHERE P.DOCKEY = @D
                      AND (P.UDF_POSTATUS IS NULL
                           OR TRIM(COALESCE(CAST(P.UDF_POSTATUS AS VARCHAR(40)), '')) = ''
                           OR UPPER(TRIM(CAST(P.UDF_POSTATUS AS VARCHAR(40)))) = 'PENDING')
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

    public async Task<IReadOnlyList<PurchaseRequestLineRow>> GetPurchaseRequestLinesAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return Array.Empty<PurchaseRequestLineRow>();

        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            throw new InvalidOperationException("TenantBootstrap:TenantCode is required to load PH_PODTL.");

        var sql = string.IsNullOrWhiteSpace(_approval.Value.PurchaseRequestLinesSql)
            ? DefaultPurchaseRequestLinesSql
            : _approval.Value.PurchaseRequestLinesSql!;

        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<PurchaseRequestLineRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapLineRow(reader));

        return list;
    }

    /// <summary>Maps UI tab status; ignores numeric or other <c>PH_PO.STATUS</c> values when <paramref name="transferable"/> is available.</summary>
    internal static string NormalizeTabStatus(string? rawFromReader, bool? transferable)
    {
        if (!string.IsNullOrWhiteSpace(rawFromReader))
        {
            if (string.Equals(rawFromReader, "Pending", StringComparison.OrdinalIgnoreCase))
                return "Pending";
            if (string.Equals(rawFromReader, "Approved", StringComparison.OrdinalIgnoreCase))
                return "Approved";
            if (string.Equals(rawFromReader, "REJECTED", StringComparison.OrdinalIgnoreCase))
                return "Rejected";
            if (string.Equals(rawFromReader, "Rejected", StringComparison.OrdinalIgnoreCase))
                return "Rejected";
            if (string.Equals(rawFromReader, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawFromReader, "Canceled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawFromReader, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                return "Cancelled";
        }

        return transferable switch
        {
            null => "Pending",
            true => "Approved",
            false => "Cancelled",
        };
    }

    private static PurchaseOrderRow MapRow(DbDataReader reader)
    {
        var po = GetString(reader, "PONUMBER", "DOCNO") ?? "";
        var vendor = GetString(reader, "VENDOR", "COMPANYNAME") ?? "";
        var amount = GetDecimal(reader, "AMOUNT", "DOCAMT");
        var description = GetString(reader, "DESCRIPTION") ?? "";
        var orderDate = GetDateTime(reader, "ORDERDATE", "DOCDATE") ?? DateTime.UtcNow.Date;
        var transferable = GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE");
        var rawStatus = (GetString(reader, "PQSTATUS", "POSTATUS", "STATUS", "UDF_POSTATUS") ?? "").Trim();
        // PH_PO.STATUS is often an integer workflow code — never use it for tabs unless it matches our labels.
        var status = NormalizeTabStatus(rawStatus, transferable);

        return new PurchaseOrderRow
        {
            DocKey = GetInt32(reader, "DOCKEY", "POKEY", "PQKEY", "ID"),
            PoNumber = po.Trim(),
            Vendor = vendor.Trim(),
            Amount = amount,
            Status = status,
            Description = description.Trim(),
            OrderDate = orderDate,
            Transferable = transferable,
        };
    }

    private static PurchaseRequestLineRow MapLineRow(DbDataReader reader)
    {
        return new PurchaseRequestLineRow
        {
            LineNo = GetInt32(reader, "LINENO", "SEQ", "LINE_NO", "LINENO"),
            ItemCode = (GetString(reader, "ITEMCODE") ?? "").Trim(),
            Description = (GetString(reader, "DESCRIPTION") ?? "").Trim(),
            Sqty = GetDecimal(reader, "SQTY"),
            SuomQty = GetDecimal(reader, "SUOMQTY"),
            Qty = GetDecimal(reader, "QTY", "QUANTITY"),
            UnitPrice = GetDecimal(reader, "UNITPRICE", "RATE"),
            LineAmount = GetDecimal(reader, "LINEAMOUNT", "AMOUNT", "TOTAL"),
            Transferable = GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE"),
        };
    }

    private static int GetInt32(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return 0;
            var v = reader.GetValue(ord);
            if (v is int i)
                return i;
            if (v is long l)
                return l > int.MaxValue ? int.MaxValue : (int)l;
            if (v is short s)
                return s;
            if (int.TryParse(v?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
                return n;
        }

        return 0;
    }

    private static string? GetString(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return null;
            return reader.GetValue(ord)?.ToString();
        }

        return null;
    }

    private static decimal GetDecimal(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return 0m;
            var v = reader.GetValue(ord);
            if (v is decimal d)
                return d;
            return Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0m;
    }

    private static DateTime? GetDateTime(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return null;
            var v = reader.GetValue(ord);
            if (v is DateTime dt)
                return dt.Date;
            if (DateTime.TryParse(v?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
                return parsed.Date;
        }

        return null;
    }

    private static bool? GetBoolNullable(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0)
                continue;
            if (reader.IsDBNull(ord))
                return null;
            var v = reader.GetValue(ord);
            if (v is bool b)
                return b;
            if (v is short s)
                return s != 0;
            if (v is int i)
                return i != 0;
            if (v is string str)
            {
                var u = str.Trim().ToUpperInvariant();
                if (u is "T" or "Y" or "1" or "TRUE" or "YES")
                    return true;
                if (u is "F" or "N" or "0" or "FALSE" or "NO")
                    return false;
            }
            if (int.TryParse(v?.ToString(), out var n))
                return n != 0;
        }

        return null;
    }

    private static int TryGetOrdinal(DbDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
