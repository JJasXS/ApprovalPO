using System.Data.Common;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Options;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services.Orders;

/// <summary>Loads goods receipts from Firebird <c>PH_GR</c> / <c>PH_GRDTL</c>.</summary>
public sealed class GoodsReceiptCatalogService : IGoodsReceiptCatalog
{
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly IOptions<ApprovalOptions> _approval;

    public GoodsReceiptCatalogService(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        IOptions<ApprovalOptions> options)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _approval = options;
    }

    public async Task<IReadOnlyList<GoodsReceiptListItem>> GetReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryHeadersAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Select(r => new GoodsReceiptListItem
            {
                DocKey = r.DocKey,
                GrNumber = r.GrNumber,
                PoNumber = r.PoNumber,
                Vendor = r.Vendor,
                GrDate = r.GrDate,
                Amount = r.Amount
            })
            .ToList();
    }

    public async Task<GoodsReceiptRow?> GetReceiptAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0) return null;
        var rows = await QueryHeadersAsync(cancellationToken, docKey).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<GoodsReceiptLineRow>> GetReceiptLinesAsync(
        int docKey,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return Array.Empty<GoodsReceiptLineRow>();

        var tenant = RequireTenant();
        var overrideSql = _approval.Value.GoodsReceiptLinesSql;
        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var sql = string.IsNullOrWhiteSpace(overrideSql)
            ? BuildLinesSql(await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GRDTL", cancellationToken).ConfigureAwait(false))
            : overrideSql;

        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<GoodsReceiptLineRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapLine(reader));

        return list;
    }

    private async Task<IReadOnlyList<GoodsReceiptRow>> QueryHeadersAsync(
        CancellationToken cancellationToken,
        int? docKey = null)
    {
        var tenant = RequireTenant();
        var overrideSql = _approval.Value.GoodsReceiptsSql;
        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var sql = string.IsNullOrWhiteSpace(overrideSql)
            ? BuildHeadersSql(await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GR", cancellationToken).ConfigureAwait(false))
            : overrideSql;

        if (docKey is > 0 && string.IsNullOrWhiteSpace(overrideSql))
        {
            sql = sql.Replace("FIRST 200", "FIRST 1", StringComparison.OrdinalIgnoreCase);
            sql = sql.Replace(
                "FROM PH_GR H",
                "FROM PH_GR H WHERE H.DOCKEY = @DocKey",
                StringComparison.OrdinalIgnoreCase);
        }
        else if (docKey is > 0)
        {
            sql = sql.Replace("FIRST 200", "FIRST 1", StringComparison.OrdinalIgnoreCase);
        }

        await using var cmd = new FbCommand(sql, conn);
        if (docKey is > 0)
            cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey.Value;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<GoodsReceiptRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapHeader(reader));

        return list;
    }

    internal static string BuildHeadersSql(HashSet<string> grCols)
    {
        var poExpr = "CAST('' AS VARCHAR(40))";
        var join = "";

        if (grCols.Contains("FROMDOCKEY"))
        {
            join = "LEFT JOIN PH_PO P ON P.DOCKEY = H.FROMDOCKEY";
            poExpr = "TRIM(COALESCE(P.DOCNO, ''))";
        }
        else if (FirebirdSchemaHelper.PickColumn(grCols, "PONO", "PONUMBER", "REFDOCNO", "FROMDOCNO") is { } poCol)
        {
            poExpr = $"TRIM(COALESCE(H.{poCol}, ''))";
        }

        return $"""
            SELECT FIRST 200
              H.DOCKEY,
              TRIM(H.DOCNO) AS GRNUMBER,
              {poExpr} AS PONUMBER,
              TRIM(COALESCE(H.COMPANYNAME, H.CODE, '')) AS VENDOR,
              COALESCE(H.DOCAMT, 0) AS AMOUNT,
              TRIM(COALESCE(CAST(H.DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
              COALESCE(H.DOCDATE, CURRENT_DATE) AS GRDATE
            FROM PH_GR H
            {join}
            ORDER BY H.DOCDATE DESC NULLS LAST, H.DOCNO DESC
            """;
    }

    internal static string BuildLinesSql(HashSet<string> dtlCols)
    {
        var receiveCol = FirebirdSchemaHelper.PickColumn(dtlCols, "RECEIVEQTY", "RECIEVEQTY", "RECEIVEDQTY") ?? "RECEIVEQTY";
        var returnCol = FirebirdSchemaHelper.PickColumn(dtlCols, "RETURNQTY") ?? "RETURNQTY";

        return $"""
            SELECT
              COALESCE(D.SEQ, 0) AS LINENO,
              TRIM(COALESCE(D.ITEMCODE, '')) AS ITEMCODE,
              TRIM(COALESCE(CAST(D.DESCRIPTION AS VARCHAR(800)), '')) AS DESCRIPTION,
              COALESCE(D.QTY, 0) AS QTY,
              COALESCE(D.{receiveCol}, 0) AS RECEIVEQTY,
              COALESCE(D.{returnCol}, 0) AS RETURNQTY
            FROM PH_GRDTL D
            WHERE D.DOCKEY = @DocKey
            ORDER BY COALESCE(D.SEQ, 0)
            """;
    }

    private string RequireTenant()
    {
        var tenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            tenant = (_configuration["TENANT_CODE"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenant))
            throw new InvalidOperationException("TenantBootstrap:TenantCode is required to load PH_GR.");
        return tenant;
    }

    private static GoodsReceiptRow MapHeader(DbDataReader reader) => new()
    {
        DocKey = GetInt32(reader, "DOCKEY"),
        GrNumber = (GetString(reader, "GRNUMBER", "DOCNO") ?? "").Trim(),
        PoNumber = (GetString(reader, "PONUMBER") ?? "").Trim(),
        Vendor = (GetString(reader, "VENDOR", "COMPANYNAME") ?? "").Trim(),
        Amount = GetDecimal(reader, "AMOUNT", "DOCAMT"),
        Description = (GetString(reader, "DESCRIPTION") ?? "").Trim(),
        GrDate = GetDateTime(reader, "GRDATE", "DOCDATE") ?? DateTime.UtcNow.Date,
    };

    private static GoodsReceiptLineRow MapLine(DbDataReader reader) => new()
    {
        LineNo = GetInt32(reader, "LINENO", "SEQ"),
        ItemCode = (GetString(reader, "ITEMCODE") ?? "").Trim(),
        Description = (GetString(reader, "DESCRIPTION") ?? "").Trim(),
        Qty = GetDecimal(reader, "QTY", "QUANTITY"),
        ReceiveQty = GetDecimal(reader, "RECEIVEQTY", "RECIEVEQTY", "RECEIVEDQTY"),
        ReturnQty = GetDecimal(reader, "RETURNQTY"),
    };

    private static int GetInt32(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0) continue;
            if (reader.IsDBNull(ord)) return 0;
            var v = reader.GetValue(ord);
            if (v is int i) return i;
            if (v is long l) return l > int.MaxValue ? int.MaxValue : (int)l;
            if (int.TryParse(v?.ToString(), out var n)) return n;
        }
        return 0;
    }

    private static string? GetString(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0) continue;
            if (reader.IsDBNull(ord)) return null;
            return reader.GetValue(ord)?.ToString();
        }
        return null;
    }

    private static decimal GetDecimal(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0) continue;
            if (reader.IsDBNull(ord)) return 0m;
            var v = reader.GetValue(ord);
            if (v is decimal d) return d;
            return Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        return 0m;
    }

    private static DateTime? GetDateTime(DbDataReader reader, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            var ord = TryGetOrdinal(reader, name);
            if (ord < 0) continue;
            if (reader.IsDBNull(ord)) return null;
            var v = reader.GetValue(ord);
            if (v is DateTime dt) return dt.Date;
            if (DateTime.TryParse(v?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
                return parsed.Date;
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
