using System.Data.Common;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Options;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services.Orders;

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

    /// <summary>Detail lines for <c>PH_PODTL</c>; parameter <c>@DocKey</c>.</summary>
    internal static string DefaultPurchaseRequestLinesSql =>
        FirebirdDocumentSqlBuilder.BuildDetailLinesSql("PH_PODTL");

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
        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load PH_PO", cancellationToken).ConfigureAwait(false);

        string sql;
        if (!string.IsNullOrWhiteSpace(_approval.Value.PurchaseOrdersSql))
        {
            sql = _approval.Value.PurchaseOrdersSql!;
        }
        else
        {
            await PhPoSchemaBootstrap.EnsureUdfPoStatusColumnAsync(conn, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var poCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false);
            sql = PhPoSqlBuilder.BuildPurchaseOrdersSql(poCols);
        }

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

        var statusText = ApprovalListStatusHelper.ParseListStatusToDb(listStatus);
        if (statusText is null)
            return (false, "ListStatus must be Pending, Approved, Cancelled, or Rejected.");

        if (!TenantConfigurationHelper.TryGetTenantCode(_configuration, out _, out var tenantError))
            return (false, tenantError);

        try
        {
            await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, cancellationToken: cancellationToken).ConfigureAwait(false);

            var statusCol = PhPoSqlBuilder.PickStatusColumn(
                await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false));
            if (statusCol is null)
            {
                if (!await PhPoSchemaBootstrap.EnsureUdfPoStatusColumnAsync(conn, cancellationToken: cancellationToken)
                        .ConfigureAwait(false))
                {
                    return (false,
                        "PH_PO.UDF_POSTATUS is not on this database and could not be created automatically. Add VARCHAR(40) column UDF_POSTATUS to PH_PO, or run eQuotation db_initializer.");
                }

                statusCol = PhPoSqlBuilder.PickStatusColumn(
                    await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false));
                if (statusCol is null)
                {
                    return (false,
                        "PH_PO.UDF_POSTATUS was not found after schema ensure. Contact your administrator.");
                }
            }

            await using var cmd = new FbCommand(
                $"UPDATE PH_PO SET {statusCol} = @S WHERE DOCKEY = @D",
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

        if (!TenantConfigurationHelper.TryGetTenantCode(_configuration, out _, out var tenantError))
            return (false, tenantError);

        try
        {
            await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, cancellationToken: cancellationToken).ConfigureAwait(false);

            var statusCol = PhPoSqlBuilder.PickStatusColumn(
                await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false));
            var pendingPredicate = statusCol is null
                ? "1=1"
                : ApprovalListStatusHelper.BuildPendingHeaderPredicate("P", statusCol);

            var sql = $"""
                UPDATE PH_PODTL D
                SET D.TRANSFERABLE = @T
                WHERE D.DOCKEY = @D
                  AND COALESCE(D.SEQ, 0) = @L
                  AND EXISTS (
                    SELECT 1 FROM PH_PO P
                    WHERE P.DOCKEY = @D
                      AND {pendingPredicate}
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

        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load PH_PODTL", cancellationToken).ConfigureAwait(false);

        var sql = string.IsNullOrWhiteSpace(_approval.Value.PurchaseRequestLinesSql)
            ? PhPoSqlBuilder.BuildDetailLinesSql(
                await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PODTL", cancellationToken).ConfigureAwait(false))
            : _approval.Value.PurchaseRequestLinesSql!;

        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<PurchaseRequestLineRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapLineRow(reader));

        return list;
    }

    private static PurchaseOrderRow MapRow(DbDataReader reader)
    {
        var po = FirebirdDataReaderHelper.GetString(reader, "PONUMBER", "DOCNO") ?? "";
        var vendor = FirebirdDataReaderHelper.GetString(reader, "VENDOR", "COMPANYNAME") ?? "";
        var amount = FirebirdDataReaderHelper.GetDecimal(reader, "AMOUNT", "DOCAMT");
        var description = FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "";
        var orderDate = FirebirdDataReaderHelper.GetDateTime(reader, "ORDERDATE", "DOCDATE") ?? DateTime.UtcNow.Date;
        var transferable = FirebirdDataReaderHelper.GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE");
        var rawStatus = (FirebirdDataReaderHelper.GetString(reader, "PQSTATUS", "POSTATUS", "STATUS", "UDF_POSTATUS") ?? "").Trim();
        // PH_PO.STATUS is often an integer workflow code — never use it for tabs unless it matches our labels.
        var status = ApprovalListStatusHelper.NormalizeTabStatus(rawStatus, transferable);

        return new PurchaseOrderRow
        {
            DocKey = FirebirdDataReaderHelper.GetInt32(reader, "DOCKEY", "POKEY", "PQKEY", "ID"),
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
            LineNo = FirebirdDataReaderHelper.GetInt32(reader, "LINENO", "SEQ", "LINE_NO", "LINENO"),
            ItemCode = (FirebirdDataReaderHelper.GetString(reader, "ITEMCODE") ?? "").Trim(),
            Description = (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),
            Sqty = FirebirdDataReaderHelper.GetDecimal(reader, "SQTY"),
            SuomQty = FirebirdDataReaderHelper.GetDecimal(reader, "SUOMQTY"),
            Qty = FirebirdDataReaderHelper.GetDecimal(reader, "QTY", "QUANTITY"),
            UnitPrice = FirebirdDataReaderHelper.GetDecimal(reader, "UNITPRICE", "RATE"),
            LineAmount = FirebirdDataReaderHelper.GetDecimal(reader, "LINEAMOUNT", "AMOUNT", "TOTAL"),
            Transferable = FirebirdDataReaderHelper.GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE"),
            Project = (FirebirdDataReaderHelper.GetString(reader, "PROJECT") ?? "").Trim(),
        };
    }
}
