using System.Globalization;
using System.Text;
using ApprovalPO.Helpers;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.Orders;

/// <summary>
/// Creates <c>PH_GR</c>, <c>PH_GRDTL</c>, and <c>ST_XTRANS</c> from an approved PO
/// (same pattern as eQuotation PR → PO in <c>procurement_purchase_order_transfer.py</c>).
/// </summary>
public sealed class PoToGoodsReceiptFirebirdTransferService
{
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PoToGoodsReceiptFirebirdTransferService> _logger;

    public PoToGoodsReceiptFirebirdTransferService(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        ILogger<PoToGoodsReceiptFirebirdTransferService> logger)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GrTransferResult> TransferAsync(
        int poDocKey,
        string poNumber,
        IReadOnlyList<GrTransferLineRequest>? lines = null,
        CancellationToken cancellationToken = default)
    {
        if (poDocKey <= 0)
            return GrTransferResult.Failure("Invalid PO document key.");

        var tenant = TenantConfigurationHelper.RequireTenantCode(_configuration);
        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var grHeaderCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GR", cancellationToken).ConfigureAwait(false);
        var grDetailCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_GRDTL", cancellationToken).ConfigureAwait(false);
        var poHeaderCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PO", cancellationToken).ConfigureAwait(false);
        var poDetailCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "PH_PODTL", cancellationToken).ConfigureAwait(false);
        var xtransCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "ST_XTRANS", cancellationToken).ConfigureAwait(false);

        var grHeaderKey = FirebirdSchemaHelper.PickColumn(grHeaderCols, "DOCKEY", "ID");
        var grDetailKey = FirebirdSchemaHelper.PickColumn(grDetailCols, "DTLKEY", "ID");
        var grDetailFk = FirebirdSchemaHelper.PickColumn(grDetailCols, "DOCKEY");
        var poHeaderKey = FirebirdSchemaHelper.PickColumn(poHeaderCols, "DOCKEY", "ID");
        var poDetailKey = FirebirdSchemaHelper.PickColumn(poDetailCols, "DTLKEY", "ID");
        var poDetailFk = FirebirdSchemaHelper.PickColumn(poDetailCols, "DOCKEY");
        var xtransKey = FirebirdSchemaHelper.PickColumn(xtransCols, "DOCKEY", "ID");

        if (grHeaderKey is null || grDetailKey is null || grDetailFk is null
            || poHeaderKey is null || poDetailKey is null || poDetailFk is null || xtransKey is null)
        {
            return GrTransferResult.Failure("Goods receipt or purchase order schema is missing required key columns.");
        }

        var grHeaderStrLen = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "PH_GR", cancellationToken).ConfigureAwait(false);
        var grDetailStrLen = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "PH_GRDTL", cancellationToken).ConfigureAwait(false);
        var xtransStrLen = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "ST_XTRANS", cancellationToken).ConfigureAwait(false);

        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var poHeader = await LoadHeaderAsync(conn, tx, "PH_PO", poHeaderKey, poDocKey, cancellationToken).ConfigureAwait(false);
            if (poHeader is null)
                return GrTransferResult.Failure($"Purchase order {poNumber} was not found.");

            var poLines = await LoadDetailsAsync(conn, tx, "PH_PODTL", poDetailFk, poDocKey, cancellationToken).ConfigureAwait(false);
            if (poLines.Count == 0)
                return GrTransferResult.Failure("PO has no detail lines to transfer.");

            var detailIds = poLines
                .Select(l => GetInt(l, poDetailKey))
                .Where(id => id > 0)
                .ToList();

            var existingQty = await FetchExistingTransferQtyMapAsync(
                conn, tx, poDocKey, detailIds, xtransCols, cancellationToken).ConfigureAwait(false);

            var partialByCode = BuildPartialRequestMap(lines);
            var transfers = new List<(Dictionary<string, object?> Source, int PoDtlKey, decimal Qty)>();
            foreach (var line in poLines)
            {
                var dtlKey = GetInt(line, poDetailKey);
                if (dtlKey <= 0) continue;

                if (!IsTransferable(line))
                    continue;

                var sourceQty = LineSourceQty(line);
                if (sourceQty <= 0) continue;

                var already = existingQty.GetValueOrDefault(dtlKey);
                var remaining = Money(sourceQty - already);
                if (remaining <= 0) continue;

                decimal transferQty;
                if (partialByCode is not null)
                {
                    var itemCode = Clean(line.GetValueOrDefault("ITEMCODE"));
                    if (string.IsNullOrEmpty(itemCode) || !partialByCode.TryGetValue(itemCode, out var requested))
                        continue;
                    transferQty = Money(Math.Min(requested, remaining));
                    if (transferQty <= 0) continue;
                }
                else
                {
                    transferQty = remaining;
                }

                transfers.Add((line, dtlKey, transferQty));
            }

            if (transfers.Count == 0)
            {
                return partialByCode is not null
                    ? GrTransferResult.Failure("No scanned items matched transferable PO lines with remaining quantity.")
                    : GrTransferResult.Failure("No outstanding PO quantity remains to receive (or lines are not transferable).");
            }

            var docno = await NextGrDocNoAsync(conn, tx, grHeaderCols, cancellationToken).ConfigureAwait(false);
            if (await GrDocNoExistsAsync(conn, tx, grHeaderCols, docno, cancellationToken).ConfigureAwait(false))
                return GrTransferResult.Failure($"Goods received number already exists: {docno}");

            var grDocKey = await FirebirdTableWriter.NextKeyAsync(
                conn, "PH_GR", grHeaderKey,
                ["GEN_PH_GR_ID", "GEN_PH_GR_DOCKEY", "GEN_PH_GR", "SEQ_PH_GR_DOCKEY"],
                cancellationToken, tx).ConfigureAwait(false);

            var effectiveDate = NormalizeDate(poHeader.GetValueOrDefault("DOCDATE")) ?? DateTime.UtcNow.Date;
            var poDocNo = Clean(poHeader.GetValueOrDefault("DOCNO")) ?? poNumber;
            var supplierCode = Clean(poHeader.GetValueOrDefault("CODE")) ?? "";
            var statusCol = FirebirdSchemaHelper.PickColumn(grHeaderCols, "STATUS");
            var grStatus = EncodeStatus(0);

            decimal totalDocAmount = 0;
            var linePayloads = new List<(Dictionary<string, object?> Detail, Dictionary<string, object?> XTrans, int PoDtlKey)>();
            var lineIndex = 0;

            foreach (var (source, poDtlKey, qty) in transfers)
            {
                lineIndex++;
                var unitPrice = Money(ToDecimal(source.GetValueOrDefault("UNITPRICE")));
                var sourceQty = LineSourceQty(source);
                var lineTax = Money(ToDecimal(source.GetValueOrDefault("TAXAMT")));
                if (sourceQty > 0 && lineTax != 0)
                    lineTax = Money((lineTax / sourceQty) * qty);
                else
                    lineTax = 0;

                var amount = Money((unitPrice * qty) + lineTax);
                totalDocAmount += amount;

                var lineSeq = source.GetValueOrDefault("SEQ") ?? lineIndex * 1000;

                var detailValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [grDetailFk] = grDocKey,
                    ["SEQ"] = lineSeq,
                    ["ITEMCODE"] = Clean(source.GetValueOrDefault("ITEMCODE")),
                    ["LOCATION"] = Clean(source.GetValueOrDefault("LOCATION")),
                    ["BATCH"] = Clean(source.GetValueOrDefault("BATCH")),
                    ["PROJECT"] = Clean(source.GetValueOrDefault("PROJECT")) ?? Clean(poHeader.GetValueOrDefault("PROJECT")) ?? "----",
                    ["DESCRIPTION"] = Clean(source.GetValueOrDefault("DESCRIPTION")),
                    ["DESCRIPTION2"] = Clean(source.GetValueOrDefault("DESCRIPTION2")),
                    ["DESCRIPTION3"] = source.GetValueOrDefault("DESCRIPTION3"),
                    ["PERMITNO"] = Clean(source.GetValueOrDefault("PERMITNO")),
                    ["QTY"] = (double)qty,
                    ["UOM"] = Clean(source.GetValueOrDefault("UOM")) ?? "UNIT",
                    ["RATE"] = (double)ToDecimal(source.GetValueOrDefault("RATE"), 1m),
                    ["SQTY"] = (double)qty,
                    ["SUOMQTY"] = (double)(Money(ToDecimal(source.GetValueOrDefault("SUOMQTY"))) > 0
                        ? ToDecimal(source.GetValueOrDefault("SUOMQTY"))
                        : qty),
                    ["OFFSETQTY"] = (double)ToDecimal(source.GetValueOrDefault("OFFSETQTY")),
                    ["UNITPRICE"] = (double)unitPrice,
                    ["DELIVERYDATE"] = NormalizeDate(source.GetValueOrDefault("DELIVERYDATE")) ?? effectiveDate,
                    ["DISC"] = Clean(source.GetValueOrDefault("DISC")),
                    ["TAX"] = Clean(source.GetValueOrDefault("TAX")),
                    ["TARIFF"] = Clean(source.GetValueOrDefault("TARIFF")),
                    ["TAXEXEMPTIONREASON"] = Clean(source.GetValueOrDefault("TAXEXEMPTIONREASON")),
                    ["IRBM_CLASSIFICATION"] = Clean(source.GetValueOrDefault("IRBM_CLASSIFICATION")),
                    ["TAXRATE"] = Clean(source.GetValueOrDefault("TAXRATE")),
                    ["TAXAMT"] = (double)lineTax,
                    ["LOCALTAXAMT"] = (double)lineTax,
                    ["EXEMPTED_TAXRATE"] = Clean(source.GetValueOrDefault("EXEMPTED_TAXRATE")),
                    ["EXEMPTED_TAXAMT"] = (double)ToDecimal(source.GetValueOrDefault("EXEMPTED_TAXAMT")),
                    ["TAXINCLUSIVE"] = CoerceBool(source.GetValueOrDefault("TAXINCLUSIVE")),
                    ["AMOUNT"] = (double)amount,
                    ["LOCALAMOUNT"] = (double)amount,
                    ["PRINTABLE"] = source.GetValueOrDefault("PRINTABLE") is null || CoerceBool(source.GetValueOrDefault("PRINTABLE")),
                    ["FROMDOCTYPE"] = SqlAccountingDocTypes.PurchaseOrder,
                    ["FROMDOCKEY"] = poDocKey,
                    ["FROMDTLKEY"] = poDtlKey,
                    ["TRANSFERABLE"] = true,
                    ["REMARK1"] = Clean(source.GetValueOrDefault("REMARK1")),
                    ["REMARK2"] = Clean(source.GetValueOrDefault("REMARK2")),
                };

                if (grDetailCols.Contains("RECEIVEQTY"))
                    detailValues["RECEIVEQTY"] = (double)qty;
                if (grDetailCols.Contains("RECIEVEQTY"))
                    detailValues["RECIEVEQTY"] = (double)qty;

                var xRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CODE"] = supplierCode,
                    ["FROMDOCTYPE"] = SqlAccountingDocTypes.PurchaseOrder,
                    ["TODOCTYPE"] = SqlAccountingDocTypes.GoodsReceived,
                    ["FROMDOCKEY"] = poDocKey,
                    ["TODOCKEY"] = grDocKey,
                    ["FROMDTLKEY"] = poDtlKey,
                    ["QTY"] = (double)qty,
                    ["SQTY"] = (double)qty,
                };
                if (xtransCols.Contains("SUOMQTY"))
                    xRow["SUOMQTY"] = (double)qty;
                if (statusCol is not null && xtransCols.Contains("TOSTATUS"))
                    xRow["TOSTATUS"] = grStatus;

                linePayloads.Add((detailValues, xRow, poDtlKey));
            }

            var headerValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [grHeaderKey] = grDocKey,
                ["DOCNO"] = docno,
                ["DOCNOEX"] = docno,
                ["DOCDATE"] = effectiveDate,
                ["POSTDATE"] = effectiveDate,
                ["TAXDATE"] = effectiveDate,
                ["CODE"] = supplierCode,
                ["COMPANYNAME"] = Clean(poHeader.GetValueOrDefault("COMPANYNAME")),
                ["ADDRESS1"] = Clean(poHeader.GetValueOrDefault("ADDRESS1")),
                ["ADDRESS2"] = Clean(poHeader.GetValueOrDefault("ADDRESS2")),
                ["ADDRESS3"] = Clean(poHeader.GetValueOrDefault("ADDRESS3")),
                ["ADDRESS4"] = Clean(poHeader.GetValueOrDefault("ADDRESS4")),
                ["POSTCODE"] = Clean(poHeader.GetValueOrDefault("POSTCODE")),
                ["CITY"] = Clean(poHeader.GetValueOrDefault("CITY")),
                ["STATE"] = Clean(poHeader.GetValueOrDefault("STATE")),
                ["COUNTRY"] = Clean(poHeader.GetValueOrDefault("COUNTRY")),
                ["PHONE1"] = Clean(poHeader.GetValueOrDefault("PHONE1")),
                ["MOBILE"] = Clean(poHeader.GetValueOrDefault("MOBILE")),
                ["FAX1"] = Clean(poHeader.GetValueOrDefault("FAX1")),
                ["ATTENTION"] = Clean(poHeader.GetValueOrDefault("ATTENTION")),
                ["AREA"] = Clean(poHeader.GetValueOrDefault("AREA")) ?? "----",
                ["AGENT"] = Clean(poHeader.GetValueOrDefault("AGENT")) ?? supplierCode,
                ["PROJECT"] = Clean(poHeader.GetValueOrDefault("PROJECT")) ?? "----",
                ["TERMS"] = Clean(poHeader.GetValueOrDefault("TERMS")),
                ["CURRENCYCODE"] = Clean(poHeader.GetValueOrDefault("CURRENCYCODE")) ?? "MYR",
                ["CURRENCYRATE"] = (double)ToDecimal(poHeader.GetValueOrDefault("CURRENCYRATE"), 1m),
                ["SHIPPER"] = Clean(poHeader.GetValueOrDefault("SHIPPER")) ?? "----",
                ["DESCRIPTION"] = $"Goods Received from {poDocNo} (ProAcc scan)",
                ["CANCELLED"] = false,
                ["STATUS"] = grStatus,
                ["DOCAMT"] = (double)Money(totalDocAmount),
                ["LOCALDOCAMT"] = (double)Money(totalDocAmount),
                ["BRANCHNAME"] = Clean(poHeader.GetValueOrDefault("BRANCHNAME")),
                ["DADDRESS1"] = Clean(poHeader.GetValueOrDefault("DADDRESS1")),
                ["DADDRESS2"] = Clean(poHeader.GetValueOrDefault("DADDRESS2")),
                ["DADDRESS3"] = Clean(poHeader.GetValueOrDefault("DADDRESS3")),
                ["DADDRESS4"] = Clean(poHeader.GetValueOrDefault("DADDRESS4")),
                ["DPOSTCODE"] = Clean(poHeader.GetValueOrDefault("DPOSTCODE")),
                ["DCITY"] = Clean(poHeader.GetValueOrDefault("DCITY")),
                ["DSTATE"] = Clean(poHeader.GetValueOrDefault("DSTATE")),
                ["DCOUNTRY"] = Clean(poHeader.GetValueOrDefault("DCOUNTRY")),
                ["DATTENTION"] = Clean(poHeader.GetValueOrDefault("DATTENTION")),
                ["DPHONE1"] = Clean(poHeader.GetValueOrDefault("DPHONE1")),
                ["DMOBILE"] = Clean(poHeader.GetValueOrDefault("DMOBILE")),
                ["DFAX1"] = Clean(poHeader.GetValueOrDefault("DFAX1")),
                ["TAXEXEMPTNO"] = Clean(poHeader.GetValueOrDefault("TAXEXEMPTNO")),
                ["SALESTAXNO"] = Clean(poHeader.GetValueOrDefault("SALESTAXNO")),
                ["SERVICETAXNO"] = Clean(poHeader.GetValueOrDefault("SERVICETAXNO")),
                ["TIN"] = Clean(poHeader.GetValueOrDefault("TIN")),
                ["IDTYPE"] = (int)ToDecimal(poHeader.GetValueOrDefault("IDTYPE")),
                ["IDNO"] = Clean(poHeader.GetValueOrDefault("IDNO")),
                ["TOURISMNO"] = Clean(poHeader.GetValueOrDefault("TOURISMNO")),
                ["SIC"] = Clean(poHeader.GetValueOrDefault("SIC")),
                ["INCOTERMS"] = Clean(poHeader.GetValueOrDefault("INCOTERMS")),
                ["SUBMISSIONTYPE"] = (int)ToDecimal(poHeader.GetValueOrDefault("SUBMISSIONTYPE")),
                ["BUSINESSUNIT"] = Clean(poHeader.GetValueOrDefault("BUSINESSUNIT")),
                ["TRANSFERABLE"] = true,
                ["UPDATECOUNT"] = 0,
                ["PRINTCOUNT"] = 0,
                ["LASTMODIFIED"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            if (grHeaderCols.Contains("FROMDOCKEY"))
                headerValues["FROMDOCKEY"] = poDocKey;
            if (grHeaderCols.Contains("FROMDOCTYPE"))
                headerValues["FROMDOCTYPE"] = SqlAccountingDocTypes.PurchaseOrder;

            await FirebirdTableWriter.InsertDynamicAsync(
                conn, "PH_GR",
                FirebirdTableWriter.FitStrings(headerValues, grHeaderStrLen),
                grHeaderCols, cancellationToken, tx).ConfigureAwait(false);

            var nextDtlKey = await FirebirdTableWriter.NextGlobalDtlKeyAsync(
                conn, grDetailKey, cancellationToken, tx).ConfigureAwait(false);

            foreach (var (detailRow, xRow, _) in linePayloads)
            {
                while (await GlobalDtlKeyInUseAsync(conn, grDetailKey, nextDtlKey, tx, cancellationToken).ConfigureAwait(false))
                    nextDtlKey++;

                detailRow[grDetailKey] = nextDtlKey;

                await FirebirdTableWriter.InsertDynamicAsync(
                    conn, "PH_GRDTL",
                    FirebirdTableWriter.FitStrings(detailRow, grDetailStrLen),
                    grDetailCols, cancellationToken, tx).ConfigureAwait(false);

                xRow[xtransKey] = await FirebirdTableWriter.NextKeyAsync(
                    conn, "ST_XTRANS", xtransKey,
                    ["GEN_ST_XTRANS_ID", "GEN_ST_XTRANS_DOCKEY", "GEN_ST_XTRANS", "SEQ_ST_XTRANS_DOCKEY"],
                    cancellationToken, tx).ConfigureAwait(false);
                xRow["TODTLKEY"] = nextDtlKey;

                await FirebirdTableWriter.InsertDynamicAsync(
                    conn, "ST_XTRANS",
                    FirebirdTableWriter.FitStrings(xRow, xtransStrLen),
                    xtransCols, cancellationToken, tx).ConfigureAwait(false);

                nextDtlKey++;
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Firebird GR transfer OK: PO {Po} (docKey {DocKey}) -> GR {Gr} ({LineCount} lines)",
                poNumber, poDocKey, docno, linePayloads.Count);

            return GrTransferResult.Success(docno);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(cancellationToken).ConfigureAwait(false); } catch { /* ignore */ }
            _logger.LogWarning(ex, "Firebird PO->GR transfer failed for PO docKey {DocKey}.", poDocKey);
            return GrTransferResult.Failure(ex.Message);
        }
    }
    private static async Task<Dictionary<string, object?>?> LoadHeaderAsync(
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

    private static async Task<List<Dictionary<string, object?>>> LoadDetailsAsync(
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

    private static Dictionary<string, object?> ReadRow(FbDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return row;
    }

    private static async Task<Dictionary<int, decimal>> FetchExistingTransferQtyMapAsync(
        FbConnection conn,
        FbTransaction tx,
        int poDocKey,
        List<int> detailIds,
        HashSet<string> xtransCols,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, decimal>();
        if (detailIds.Count == 0) return result;

        var fromDocType = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCTYPE");
        var fromDocKey = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDOCKEY");
        var fromDtlKey = FirebirdSchemaHelper.PickColumn(xtransCols, "FROMDTLKEY");
        var qtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "QTY");
        var sqtyCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SQTY");
        var suomCol = FirebirdSchemaHelper.PickColumn(xtransCols, "SUOMQTY");

        if (fromDocType is null || fromDocKey is null || fromDtlKey is null
            || (qtyCol is null && sqtyCol is null && suomCol is null))
            return result;

        string quantityExpr;
        if (suomCol is not null && sqtyCol is not null && qtyCol is not null)
            quantityExpr = $"COALESCE(NULLIF({suomCol}, 0), NULLIF({sqtyCol}, 0), COALESCE({qtyCol}, 0), 0)";
        else if (sqtyCol is not null && qtyCol is not null)
            quantityExpr = $"COALESCE(NULLIF({sqtyCol}, 0), COALESCE({qtyCol}, 0), 0)";
        else
            quantityExpr = suomCol ?? sqtyCol ?? qtyCol!;

        var placeholders = string.Join(", ", detailIds.Select((_, i) => $"@D{i}"));
        var sql = $"""
            SELECT {fromDtlKey}, SUM(CAST({quantityExpr} AS DOUBLE PRECISION))
            FROM ST_XTRANS
            WHERE {fromDocType} = @FromType
              AND {fromDocKey} = @FromKey
              AND {fromDtlKey} IN ({placeholders})
            GROUP BY {fromDtlKey}
            """;

        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@FromType", FbDbType.VarChar).Value = SqlAccountingDocTypes.PurchaseOrder;
        cmd.Parameters.Add("@FromKey", FbDbType.Integer).Value = poDocKey;
        for (var i = 0; i < detailIds.Count; i++)
            cmd.Parameters.Add($"@D{i}", FbDbType.Integer).Value = detailIds[i];

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var key = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var qty = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            result[key] = Money(qty);
        }

        return result;
    }

    private static async Task<string> NextGrDocNoAsync(
        FbConnection conn,
        FbTransaction tx,
        HashSet<string> grHeaderCols,
        CancellationToken cancellationToken)
    {
        var docnoCol = FirebirdSchemaHelper.PickColumn(grHeaderCols, "DOCNO");
        var prefix = $"GR-{DateTime.UtcNow:yyyyMMdd}-";
        if (docnoCol is null)
            return $"{prefix}0001";

        var sql = $"""
            SELECT FIRST 1 {docnoCol}
            FROM PH_GR
            WHERE {docnoCol} LIKE @P
            ORDER BY {docnoCol} DESC
            """;
        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@P", FbDbType.VarChar).Value = prefix + "%";
        var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (obj is null or DBNull)
            return $"{prefix}0001";

        var last = Clean(obj) ?? "";
        try
        {
            var sequence = int.Parse(last.Split('-')[^1], CultureInfo.InvariantCulture) + 1;
            return $"{prefix}{sequence:D4}";
        }
        catch
        {
            return $"{prefix}0001";
        }
    }

    private static async Task<bool> GrDocNoExistsAsync(
        FbConnection conn,
        FbTransaction tx,
        HashSet<string> grHeaderCols,
        string docno,
        CancellationToken cancellationToken)
    {
        var docnoCol = FirebirdSchemaHelper.PickColumn(grHeaderCols, "DOCNO");
        if (docnoCol is null || string.IsNullOrWhiteSpace(docno))
            return false;

        var sql = $"SELECT FIRST 1 {docnoCol} FROM PH_GR WHERE {docnoCol} = @N";
        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@N", FbDbType.VarChar).Value = docno.Trim();
        var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return obj is not null and not DBNull;
    }

    private static decimal LineSourceQty(IReadOnlyDictionary<string, object?> line)
    {
        var suom = Money(ToDecimal(line.GetValueOrDefault("SUOMQTY")));
        if (suom > 0) return suom;
        var sqty = Money(ToDecimal(line.GetValueOrDefault("SQTY")));
        if (sqty > 0) return sqty;
        return Money(ToDecimal(line.GetValueOrDefault("QTY")));
    }

    private static bool IsTransferable(IReadOnlyDictionary<string, object?> line)
    {
        if (!line.TryGetValue("TRANSFERABLE", out var v) || v is null)
            return true;
        return CoerceBool(v);
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> row, string col)
    {
        if (!row.TryGetValue(col, out var v) || v is null) return 0;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static decimal ToDecimal(object? v, decimal fallback = 0m)
    {
        if (v is null or DBNull) return fallback;
        try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static decimal Money(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    private static bool CoerceBool(object? v)
    {
        if (v is true or 1) return true;
        var t = (v?.ToString() ?? "").Trim().ToLowerInvariant();
        return t is "true" or "1" or "t" or "y" or "yes";
    }

    private static string? Clean(object? v)
    {
        var s = v?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static DateTime? NormalizeDate(object? v)
    {
        if (v is null or DBNull) return null;
        if (v is DateTime dt) return dt.Date;
        if (DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var p))
            return p.Date;
        return null;
    }

    private static int EncodeStatus(int numericDraft) => numericDraft;

    private static Dictionary<string, decimal>? BuildPartialRequestMap(IReadOnlyList<GrTransferLineRequest>? lines)
    {
        if (lines is null || lines.Count == 0)
            return null;

        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var code = (line.ItemCode ?? "").Trim();
            if (code.Length == 0 || line.Quantity <= 0) continue;
            if (map.TryGetValue(code, out var existing))
                map[code] = Money(existing + line.Quantity);
            else
                map[code] = Money(line.Quantity);
        }

        return map.Count == 0 ? null : map;
    }

    private static Task<bool> GlobalDtlKeyInUseAsync(
        FbConnection conn,
        string detailKeyCol,
        long candidate,
        FbTransaction tx,
        CancellationToken cancellationToken) =>
        FirebirdTableWriter.GlobalDtlKeyExistsAsync(conn, detailKeyCol, candidate, tx, cancellationToken);
}
