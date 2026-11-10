using System.Data.Common;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.Orders;

/// <summary>Loads delivery orders from Firebird <c>SL_DO</c> / <c>SL_DODTL</c>.</summary>
public sealed class DeliveryOrderCatalogService : IDeliveryOrderCatalog
{
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;

    public DeliveryOrderCatalogService(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<DeliveryOrderListItem>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryHeadersAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Select(r => new DeliveryOrderListItem
            {
                DocKey = r.DocKey,
                DoNumber = r.DoNumber,
                SoNumber = r.SoNumber,
                Customer = r.Customer,
                DoDate = r.DoDate,
                Amount = r.Amount
            })
            .ToList();
    }

    public async Task<DeliveryOrderRow?> GetOrderAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0) return null;
        var rows = await QueryHeadersAsync(cancellationToken, docKey).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<DeliveryOrderLineRow>> GetOrderLinesAsync(
        int docKey,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return Array.Empty<DeliveryOrderLineRow>();

        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load SL_DODTL", cancellationToken).ConfigureAwait(false);
        var sql = SlDoSqlBuilder.BuildLinesSql(
            await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_DODTL", cancellationToken).ConfigureAwait(false));

        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<DeliveryOrderLineRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapLine(reader));

        return list;
    }

    private async Task<IReadOnlyList<DeliveryOrderRow>> QueryHeadersAsync(
        CancellationToken cancellationToken,
        int? docKey = null)
    {
        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load SL_DO", cancellationToken).ConfigureAwait(false);

        var sql = SlDoSqlBuilder.BuildHeadersSql(
            await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_DO", cancellationToken).ConfigureAwait(false),
            await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_DODTL", cancellationToken).ConfigureAwait(false),
            await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "ST_XTRANS", cancellationToken).ConfigureAwait(false));

        if (docKey is > 0)
        {
            sql = sql.Replace("FIRST 200", "FIRST 1", StringComparison.OrdinalIgnoreCase);
            sql = sql.Replace(
                "ORDER BY",
                "WHERE H.DOCKEY = @DocKey\r\n            ORDER BY",
                StringComparison.OrdinalIgnoreCase);
        }

        await using var cmd = new FbCommand(sql, conn);
        if (docKey is > 0)
            cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey.Value;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<DeliveryOrderRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapHeader(reader));

        return list;
    }

    private static DeliveryOrderRow MapHeader(DbDataReader reader) => new()
    {
        DocKey = FirebirdDataReaderHelper.GetInt32(reader, "DOCKEY"),
        DoNumber = (FirebirdDataReaderHelper.GetString(reader, "DONUMBER", "DOCNO") ?? "").Trim(),
        SoNumber = (FirebirdDataReaderHelper.GetString(reader, "SONUMBER") ?? "").Trim(),
        Customer = (FirebirdDataReaderHelper.GetString(reader, "CUSTOMER", "COMPANYNAME") ?? "").Trim(),
        Amount = FirebirdDataReaderHelper.GetDecimal(reader, "AMOUNT", "DOCAMT"),
        Description = (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),
        DoDate = FirebirdDataReaderHelper.GetDateTime(reader, "DODATE", "DOCDATE") ?? DateTime.UtcNow.Date,
    };

    private static DeliveryOrderLineRow MapLine(DbDataReader reader) => new()
    {
        LineNo = FirebirdDataReaderHelper.GetInt32(reader, "LINENO", "SEQ"),
        ItemCode = (FirebirdDataReaderHelper.GetString(reader, "ITEMCODE") ?? "").Trim(),
        Description = (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),
        Qty = FirebirdDataReaderHelper.GetDecimal(reader, "QTY", "QUANTITY"),
        ReceiveQty = FirebirdDataReaderHelper.GetDecimal(reader, "RECEIVEQTY", "RECIEVEQTY", "RECEIVEDQTY"),
        ReturnQty = FirebirdDataReaderHelper.GetDecimal(reader, "RETURNQTY"),
    };
}
