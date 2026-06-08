using System.Data.Common;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Options;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services.Orders;

/// <summary>Loads goods receipts from Firebird <c>PH_GR</c> / <c>PH_GRDTL</c>.</summary>
public sealed class GoodsReceiptCatalogService : IGoodsReceiptCatalog
{
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly IOptions<ApprovalOptions> _approval;
    private readonly IMemoryCache _cache;

    public GoodsReceiptCatalogService(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        IOptions<ApprovalOptions> options,
        IMemoryCache cache)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _approval = options;
        _cache = cache;
    }

    public async Task<IReadOnlyList<GoodsReceiptListItem>> GetReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var tenant = TenantConfigurationHelper.GetTenantCodeOrEmpty(_configuration);
        var cacheSeconds = _approval.Value.GoodsReceiptListCacheSeconds;
        var cacheKey = $"gr:list:{tenant}";

        if (cacheSeconds > 0 && _cache.TryGetValue(cacheKey, out IReadOnlyList<GoodsReceiptListItem>? cached) && cached is not null)
            return cached;

        var rows = await QueryHeadersAsync(cancellationToken).ConfigureAwait(false);
        var list = rows
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

        if (cacheSeconds > 0)
        {
            _cache.Set(
                cacheKey,
                list,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(cacheSeconds) });
        }

        return list;
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

        var overrideSql = _approval.Value.GoodsReceiptLinesSql;
        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load PH_GR", cancellationToken).ConfigureAwait(false);

        var sql = string.IsNullOrWhiteSpace(overrideSql)
            ? PhGrSqlBuilder.BuildLinesSql(await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GRDTL", cancellationToken).ConfigureAwait(false))
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
        var overrideSql = _approval.Value.GoodsReceiptsSql;
        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load PH_GR", cancellationToken).ConfigureAwait(false);

        var sql = string.IsNullOrWhiteSpace(overrideSql)
            ? PhGrSqlBuilder.BuildHeadersSql(
                await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GR", cancellationToken).ConfigureAwait(false),
                await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GRDTL", cancellationToken).ConfigureAwait(false),
                await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "ST_XTRANS", cancellationToken).ConfigureAwait(false))
            : overrideSql;

        if (docKey is > 0 && string.IsNullOrWhiteSpace(overrideSql))
        {
            sql = sql.Replace("FIRST 200", "FIRST 1", StringComparison.OrdinalIgnoreCase);
            // WHERE must follow JOINs, not sit between FROM and LEFT JOIN.
            sql = sql.Replace(
                "ORDER BY",
                "WHERE H.DOCKEY = @DocKey\r\n            ORDER BY",
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

    private static GoodsReceiptRow MapHeader(DbDataReader reader) => new()
    {
        DocKey = FirebirdDataReaderHelper.GetInt32(reader, "DOCKEY"),
        GrNumber = (FirebirdDataReaderHelper.GetString(reader, "GRNUMBER", "DOCNO") ?? "").Trim(),
        PoNumber = (FirebirdDataReaderHelper.GetString(reader, "PONUMBER") ?? "").Trim(),
        Vendor = (FirebirdDataReaderHelper.GetString(reader, "VENDOR", "COMPANYNAME") ?? "").Trim(),
        Amount = FirebirdDataReaderHelper.GetDecimal(reader, "AMOUNT", "DOCAMT"),
        Description = (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),
        GrDate = FirebirdDataReaderHelper.GetDateTime(reader, "GRDATE", "DOCDATE") ?? DateTime.UtcNow.Date,
    };

    private static GoodsReceiptLineRow MapLine(DbDataReader reader) => new()
    {
        LineNo = FirebirdDataReaderHelper.GetInt32(reader, "LINENO", "SEQ"),
        ItemCode = (FirebirdDataReaderHelper.GetString(reader, "ITEMCODE") ?? "").Trim(),
        Description = (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),
        Qty = FirebirdDataReaderHelper.GetDecimal(reader, "QTY", "QUANTITY"),
        ReceiveQty = FirebirdDataReaderHelper.GetDecimal(reader, "RECEIVEQTY", "RECIEVEQTY", "RECEIVEDQTY"),
        ReturnQty = FirebirdDataReaderHelper.GetDecimal(reader, "RETURNQTY"),
    };
}
