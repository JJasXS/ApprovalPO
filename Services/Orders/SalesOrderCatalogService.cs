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

    internal const string SoStatusColumn = "UDF_SOSTATUS";



    internal static string DefaultSalesOrdersSql

    {

        get

        {

            var (transferableExpr, statusLabelExpr) =

                ApprovalListStatusHelper.BuildStatusSelectExpressions(SoStatusColumn);

            return $"""

                SELECT FIRST 200

                  DOCKEY,

                  TRIM(DOCNO) AS SONUMBER,

                  TRIM(COALESCE(COMPANYNAME, CODE, '')) AS CUSTOMER,

                  COALESCE(DOCAMT, 0) AS AMOUNT,

                  TRIM(COALESCE(CAST(DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,

                  COALESCE(DOCDATE, CURRENT_DATE) AS ORDERDATE,

                  {transferableExpr} AS TRANSFERABLEINT,

                  {statusLabelExpr} AS SOSTATUS

                FROM SL_SO

                ORDER BY DOCDATE DESC NULLS LAST, DOCNO DESC

                """;

        }

    }



    internal static string DefaultSalesOrderLinesSql =>

        FirebirdDocumentSqlBuilder.BuildDetailLinesSql("SL_SODTL");



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

        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load SL_SO", cancellationToken).ConfigureAwait(false);



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



        var statusText = ApprovalListStatusHelper.ParseListStatusToDb(listStatus);

        if (statusText is null)

            return (false, "ListStatus must be Pending, Approved, Cancelled, or Rejected.");



        if (!TenantConfigurationHelper.TryGetTenantCode(_configuration, out _, out var tenantError))

            return (false, tenantError);



        try

        {

            await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, cancellationToken: cancellationToken).ConfigureAwait(false);



            await using var cmd = new FbCommand(

                $"UPDATE SL_SO SET {SoStatusColumn} = @S WHERE DOCKEY = @D",

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



        if (!TenantConfigurationHelper.TryGetTenantCode(_configuration, out _, out var tenantError))

            return (false, tenantError);



        try

        {

            await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, cancellationToken: cancellationToken).ConfigureAwait(false);



            var pendingPredicate = ApprovalListStatusHelper.BuildPendingHeaderPredicate("P", SoStatusColumn);

            var sql = $"""

                UPDATE SL_SODTL D

                SET D.TRANSFERABLE = @T

                WHERE D.DOCKEY = @D

                  AND COALESCE(D.SEQ, 0) = @L

                  AND EXISTS (

                    SELECT 1 FROM SL_SO P

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



    public async Task<IReadOnlyList<SalesOrderLineRow>> GetSalesOrderLinesAsync(int docKey, CancellationToken cancellationToken = default)

    {

        if (docKey <= 0)

            return Array.Empty<SalesOrderLineRow>();



        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load SL_SODTL", cancellationToken).ConfigureAwait(false);



        await using var cmd = new FbCommand(DefaultSalesOrderLinesSql, conn);

        cmd.Parameters.Add("@DocKey", FbDbType.Integer).Value = docKey;



        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<SalesOrderLineRow>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))

            list.Add(MapLineRow(reader));



        return list;

    }



    private static SalesOrderRow MapRow(DbDataReader reader)

    {

        var so       = FirebirdDataReaderHelper.GetString(reader, "SONUMBER", "DOCNO") ?? "";

        var customer = FirebirdDataReaderHelper.GetString(reader, "CUSTOMER", "COMPANYNAME") ?? "";

        var amount   = FirebirdDataReaderHelper.GetDecimal(reader, "AMOUNT", "DOCAMT");

        var desc     = FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "";

        var date     = FirebirdDataReaderHelper.GetDateTime(reader, "ORDERDATE", "DOCDATE") ?? DateTime.UtcNow.Date;

        var transfer = FirebirdDataReaderHelper.GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE");

        var rawStatus = (FirebirdDataReaderHelper.GetString(reader, "SOSTATUS", SoStatusColumn, "STATUS") ?? "").Trim();

        var status   = ApprovalListStatusHelper.NormalizeTabStatus(rawStatus, transfer);



        return new SalesOrderRow

        {

            DocKey      = FirebirdDataReaderHelper.GetInt32(reader, "DOCKEY", "SOKEY", "ID"),

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

            LineNo      = FirebirdDataReaderHelper.GetInt32(reader, "LINENO", "SEQ"),

            ItemCode    = (FirebirdDataReaderHelper.GetString(reader, "ITEMCODE") ?? "").Trim(),

            Description = (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),

            Sqty        = FirebirdDataReaderHelper.GetDecimal(reader, "SQTY"),

            SuomQty     = FirebirdDataReaderHelper.GetDecimal(reader, "SUOMQTY"),

            Qty         = FirebirdDataReaderHelper.GetDecimal(reader, "QTY", "QUANTITY"),

            UnitPrice   = FirebirdDataReaderHelper.GetDecimal(reader, "UNITPRICE", "RATE"),

            LineAmount  = FirebirdDataReaderHelper.GetDecimal(reader, "LINEAMOUNT", "AMOUNT", "TOTAL"),

            Transferable = FirebirdDataReaderHelper.GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE"),

        };

}


