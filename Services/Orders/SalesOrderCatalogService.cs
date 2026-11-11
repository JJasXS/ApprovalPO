using System.Data.Common;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Auth;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.Orders;

/// <summary>
/// Loads sales orders from Firebird <c>SL_SO</c> for the configured tenant.
/// <c>SL_SO.UDF_SOSTATUS</c> (<c>PENDING</c> / <c>APPROVED</c> / <c>CANCELLED</c> / <c>REJECTED</c>) drives tabs.
/// </summary>
public sealed class SalesOrderCatalogService : ISalesOrderCatalog
{
    internal const string SoStatusColumn = SlSoSchemaBootstrap.StatusColumnName;

    internal static string DefaultSalesOrdersSql =>
        BuildSalesOrdersSql(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SoStatusColumn });

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

    internal static string BuildSalesOrdersSql(HashSet<string> soCols)
    {
        var statusCol = SlSoSchemaBootstrap.PickStatusColumn(soCols);
        var (transferableExpr, statusLabelExpr) =
            ApprovalListStatusHelper.BuildStatusSelectExpressions(statusCol);

        var slotSelect = BuildApprovalSlotSelectList(soCols);
        var slotSql = slotSelect.Length == 0 ? "" : ",\n              " + slotSelect;

        return $"""
            SELECT FIRST 200
              DOCKEY,
              TRIM(DOCNO) AS SONUMBER,
              TRIM(COALESCE(COMPANYNAME, CODE, '')) AS CUSTOMER,
              COALESCE(DOCAMT, 0) AS AMOUNT,
              TRIM(COALESCE(CAST(DESCRIPTION AS VARCHAR(2000)), '')) AS DESCRIPTION,
              COALESCE(DOCDATE, CURRENT_DATE) AS ORDERDATE,
              {transferableExpr} AS TRANSFERABLEINT,
              {statusLabelExpr} AS SOSTATUS{slotSql}
            FROM SL_SO
            ORDER BY DOCDATE DESC NULLS LAST, DOCNO DESC
            """;
    }

    public async Task<IReadOnlyList<SalesOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load SL_SO", cancellationToken).ConfigureAwait(false);
        await SlSoSchemaBootstrap.EnsureUdfSoStatusColumnAsync(conn, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await SlSoSchemaBootstrap.EnsureSoApprovalSlotColumnsAsync(conn, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var soCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SO", cancellationToken).ConfigureAwait(false);
        var sql = BuildSalesOrdersSql(soCols);

        await using var cmd = new FbCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<SalesOrderRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(MapRow(reader));

        return list;
    }

    public async Task<decimal?> GetHeaderAmountAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return null;

        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load SL_SO amount", cancellationToken).ConfigureAwait(false);
        return await DocumentHeaderAmount.ReadAsync(conn, "SL_SO", docKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PoApprovalSlots?> GetApprovalSlotsAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return null;

        await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, "load SL_SO slots", cancellationToken)
            .ConfigureAwait(false);
        await SlSoSchemaBootstrap.EnsureSoApprovalSlotColumnsAsync(conn, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var soCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SO", cancellationToken).ConfigureAwait(false);
        return await ReadApprovalSlotsForDocAsync(conn, soCols, docKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool Success, string? ErrorMessage, HeaderStatusChangeResult? Result)> TrySetHeaderListStatusAsync(
        int docKey,
        string listStatus,
        string? approverEmail = null,
        int approverUserLevel = 0,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return (false, "Invalid document key.", null);

        var statusText = ApprovalListStatusHelper.ParseListStatusToDb(listStatus);
        if (statusText is null)
            return (false, "ListStatus must be Pending, Approved, Cancelled, or Rejected.", null);

        if (!TenantConfigurationHelper.TryGetTenantCode(_configuration, out _, out var tenantError))
            return (false, tenantError, null);

        try
        {
            await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await SlSoSchemaBootstrap.EnsureUdfSoStatusColumnAsync(conn, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await SlSoSchemaBootstrap.EnsureSoApprovalSlotColumnsAsync(conn, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var soCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SO", cancellationToken).ConfigureAwait(false);
            var statusCol = SlSoSchemaBootstrap.PickStatusColumn(soCols);
            if (statusCol is null)
                return (false, "SL_SO.UDF_SOSTATUS is not on this database and could not be created automatically.", null);

            if (statusText == ApprovalListStatusHelper.ApprovedDb)
            {
                return await TryRecordApprovalAsync(
                    conn, soCols, statusCol, docKey, approverEmail, approverUserLevel, cancellationToken).ConfigureAwait(false);
            }

            var clearSlots = statusText is ApprovalListStatusHelper.RejectedDb
                or ApprovalListStatusHelper.CancelledDb
                or ApprovalListStatusHelper.PendingDb;

            var sql = BuildHeaderStatusUpdateSql(statusCol, soCols, clearSlots);
            await using var cmd = new FbCommand(sql, conn);
            cmd.Parameters.Add("@S", FbDbType.VarChar, 20).Value = statusText;
            cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;

            var n = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return (false, "No row was updated (DOCKEY not found or UDF_SOSTATUS column mismatch).", null);

            return (true, null, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    private async Task<(bool Success, string? ErrorMessage, HeaderStatusChangeResult? Result)> TryRecordApprovalAsync(
        FbConnection conn,
        HashSet<string> soCols,
        string statusCol,
        int docKey,
        string? approverEmail,
        int approverUserLevel,
        CancellationToken cancellationToken)
    {
        var email = (approverEmail ?? "").Trim();
        if (email.Length == 0)
            return (false, "Approver email is required.", null);

        var amount = await DocumentHeaderAmount.ReadAsync(conn, "SL_SO", docKey, cancellationToken).ConfigureAwait(false);
        if (amount is null)
            return (false, "Document not found.", null);

        var slots = await ReadApprovalSlotsForDocAsync(conn, soCols, docKey, cancellationToken).ConfigureAwait(false)
                    ?? new PoApprovalSlots();

        var required = ApprovalLevelWorkflow.RequiredLevels(amount.Value);
        var effectiveLevel = approverUserLevel >= 1 && approverUserLevel <= 3 ? approverUserLevel : 1;
        var slotsToFill = ApprovalLevelWorkflow.SlotsToFill(slots, effectiveLevel).ToList();

        if (slotsToFill.Count == 0 && ApprovalLevelWorkflow.CompletedLevels(slots) >= required)
            return (false, "This sales order already has all required approvals.", null);

        var now = DateTime.UtcNow;
        var setClauses = new List<string> { $"{statusCol} = @S" };
        foreach (var lvl in slotsToFill)
        {
            var ec = SlotEmailColumn(lvl);
            var dc = SlotDateColumn(lvl);
            if (soCols.Contains(ec)) setClauses.Add($"{ec} = @E{lvl}");
            if (soCols.Contains(dc)) setClauses.Add($"{dc} = @T{lvl}");
        }

        var completed = ApprovalLevelWorkflow.CompletedLevelsAfterSign(slots, effectiveLevel);
        var isFinal = completed >= required;
        var dbStatus = isFinal ? ApprovalListStatusHelper.ApprovedDb : ApprovalListStatusHelper.PendingDb;

        var whereParts = new List<string> { "DOCKEY = @D" };
        foreach (var lvl in slotsToFill)
        {
            var ec = SlotEmailColumn(lvl);
            if (soCols.Contains(ec))
                whereParts.Add($"(TRIM(COALESCE({ec}, '')) = '')");
        }

        var sql = $"UPDATE SL_SO SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", whereParts)}";
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = new FbCommand(sql, conn, (FbTransaction)tx);
            cmd.Parameters.Add("@S", FbDbType.VarChar, 20).Value = dbStatus;
            foreach (var lvl in slotsToFill)
            {
                cmd.Parameters.Add($"@E{lvl}", FbDbType.VarChar, 80).Value = email;
                cmd.Parameters.Add($"@T{lvl}", FbDbType.TimeStamp).Value = now;
            }
            cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;

            var n = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return (false, "Approval conflict: another user updated this SO. Refresh and try again.", null);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        return (true, null, new HeaderStatusChangeResult
        {
            CompletedLevels = completed,
            RequiredLevels = required,
            IsFinal = isFinal,
            Message = ApprovalLevelWorkflow.BuildProgressMessage(completed, required, 0, isFinal)
        });
    }

    private static async Task<PoApprovalSlots?> ReadApprovalSlotsForDocAsync(
        FbConnection conn,
        HashSet<string> soCols,
        int docKey,
        CancellationToken cancellationToken)
    {
        var select = BuildApprovalSlotSelectList(soCols);
        if (select.Length == 0)
            return new PoApprovalSlots();

        await using var cmd = new FbCommand($"SELECT {select} FROM SL_SO WHERE DOCKEY = @D", conn);
        cmd.Parameters.Add("@D", FbDbType.Integer).Value = docKey;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return MapApprovalSlots(reader);
    }

    private static PoApprovalSlots MapApprovalSlots(DbDataReader reader)
    {
        string? Email(string col) =>
            NullIfBlank(FirebirdDataReaderHelper.GetString(reader, col));
        DateTime? At(string col) => FirebirdDataReaderHelper.GetDateTime(reader, col);

        return new PoApprovalSlots
        {
            Level1Email = Email("UDF_SOAPPL1"),
            Level1At = At("UDF_SOAPPL1DT"),
            Level2Email = Email("UDF_SOAPPL2"),
            Level2At = At("UDF_SOAPPL2DT"),
            Level3Email = Email("UDF_SOAPPL3"),
            Level3At = At("UDF_SOAPPL3DT"),
        };
    }

    private static string? NullIfBlank(string? value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 ? null : v;
    }

    private static string BuildApprovalSlotSelectList(HashSet<string> soCols)
    {
        var parts = new List<string>();
        foreach (var col in new[] { "UDF_SOAPPL1", "UDF_SOAPPL1DT", "UDF_SOAPPL2", "UDF_SOAPPL2DT", "UDF_SOAPPL3", "UDF_SOAPPL3DT" })
        {
            if (soCols.Contains(col))
                parts.Add(col);
        }
        return string.Join(", ", parts);
    }

    private static string BuildHeaderStatusUpdateSql(string statusCol, HashSet<string> soCols, bool clearSlots)
    {
        if (!clearSlots || !soCols.Contains("UDF_SOAPPL1"))
            return $"UPDATE SL_SO SET {statusCol} = @S WHERE DOCKEY = @D";

        return $"""
            UPDATE SL_SO
            SET {statusCol} = @S,
                UDF_SOAPPL1 = NULL,
                UDF_SOAPPL1DT = NULL,
                UDF_SOAPPL2 = NULL,
                UDF_SOAPPL2DT = NULL,
                UDF_SOAPPL3 = NULL,
                UDF_SOAPPL3DT = NULL
            WHERE DOCKEY = @D
            """;
    }

    private static string SlotEmailColumn(int level) => level switch
    {
        1 => "UDF_SOAPPL1",
        2 => "UDF_SOAPPL2",
        3 => "UDF_SOAPPL3",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    private static string SlotDateColumn(int level) => level switch
    {
        1 => "UDF_SOAPPL1DT",
        2 => "UDF_SOAPPL2DT",
        3 => "UDF_SOAPPL3DT",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

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
            await SlSoSchemaBootstrap.EnsureUdfSoStatusColumnAsync(conn, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var soCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "SL_SO", cancellationToken).ConfigureAwait(false);
            var statusCol = SlSoSchemaBootstrap.PickStatusColumn(soCols);
            if (statusCol is null)
                return (false, "SL_SO.UDF_SOSTATUS is not on this database and could not be created automatically.");

            var pendingPredicate = ApprovalListStatusHelper.BuildPendingHeaderPredicate("P", statusCol);
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
        var so = FirebirdDataReaderHelper.GetString(reader, "SONUMBER", "DOCNO") ?? "";
        var customer = FirebirdDataReaderHelper.GetString(reader, "CUSTOMER", "COMPANYNAME") ?? "";
        var amount = FirebirdDataReaderHelper.GetDecimal(reader, "AMOUNT", "DOCAMT");
        var desc = FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "";
        var date = FirebirdDataReaderHelper.GetDateTime(reader, "ORDERDATE", "DOCDATE") ?? DateTime.UtcNow.Date;
        var transfer = FirebirdDataReaderHelper.GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE");
        var rawStatus = (FirebirdDataReaderHelper.GetString(reader, "SOSTATUS", SoStatusColumn, "STATUS") ?? "").Trim();
        var status = ApprovalListStatusHelper.NormalizeTabStatus(rawStatus, transfer);
        var slots = MapApprovalSlots(reader);
        var required = ApprovalLevelWorkflow.RequiredLevels(amount);
        var completed = ApprovalLevelWorkflow.CompletedLevels(slots);
        var nextLevel = status == "Pending" ? ApprovalLevelWorkflow.NextRequiredLevel(slots) : 0;

        return new SalesOrderRow
        {
            DocKey = FirebirdDataReaderHelper.GetInt32(reader, "DOCKEY", "SOKEY", "ID"),
            SoNumber = so.Trim(),
            Customer = customer.Trim(),
            Amount = amount,
            Status = status,
            Description = desc.Trim(),
            OrderDate = date,
            Transferable = transfer,
            ApprovalLevel1 = slots.Level1Email,
            ApprovalLevel1At = slots.Level1At,
            ApprovalLevel2 = slots.Level2Email,
            ApprovalLevel2At = slots.Level2At,
            ApprovalLevel3 = slots.Level3Email,
            ApprovalLevel3At = slots.Level3At,
            RequiredLevels = required,
            CompletedLevels = completed,
            NextLevel = nextLevel,
        };
    }

    private static SalesOrderLineRow MapLineRow(DbDataReader reader) =>
        new SalesOrderLineRow
        {
            LineNo = FirebirdDataReaderHelper.GetInt32(reader, "LINENO", "SEQ"),
            ItemCode = (FirebirdDataReaderHelper.GetString(reader, "ITEMCODE") ?? "").Trim(),
            Description = (FirebirdDataReaderHelper.GetString(reader, "DESCRIPTION") ?? "").Trim(),
            Sqty = FirebirdDataReaderHelper.GetDecimal(reader, "SQTY"),
            SuomQty = FirebirdDataReaderHelper.GetDecimal(reader, "SUOMQTY"),
            Qty = FirebirdDataReaderHelper.GetDecimal(reader, "QTY", "QUANTITY"),
            UnitPrice = FirebirdDataReaderHelper.GetDecimal(reader, "UNITPRICE", "RATE"),
            LineAmount = FirebirdDataReaderHelper.GetDecimal(reader, "LINEAMOUNT", "AMOUNT", "TOTAL"),
            Transferable = FirebirdDataReaderHelper.GetBoolNullable(reader, "TRANSFERABLEINT", "TRANSFERABLE"),
        };
}
