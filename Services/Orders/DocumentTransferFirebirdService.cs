using ApprovalPO.Helpers;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.Orders;

/// <summary>
/// Creates a target document + detail + <c>ST_XTRANS</c> rows from a source document
/// (PO→GR, SO→DO). Spec-driven so both flows share one path.
/// </summary>
public sealed class DocumentTransferFirebirdService
{
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentTransferFirebirdService> _logger;

    public DocumentTransferFirebirdService(
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        ILogger<DocumentTransferFirebirdService> logger)
    {
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GrTransferResult> TransferAsync(
        DocumentTransferSpec spec,
        int sourceDocKey,
        string sourceDocNo,
        IReadOnlyList<GrTransferLineRequest>? lines = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceDocKey <= 0)
            return GrTransferResult.Failure("Invalid source document key.");

        var tenant = TenantConfigurationHelper.RequireTenantCode(_configuration);
        var connStr = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);

        await using var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var targetHeaderCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, spec.TargetHeaderTable, cancellationToken).ConfigureAwait(false);
        var targetDetailCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, spec.TargetDetailTable, cancellationToken).ConfigureAwait(false);
        var sourceHeaderCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, spec.SourceHeaderTable, cancellationToken).ConfigureAwait(false);
        var sourceDetailCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, spec.SourceDetailTable, cancellationToken).ConfigureAwait(false);
        var xtransCols = await FirebirdSchemaHelper.GetColumnNamesAsync(conn, "ST_XTRANS", cancellationToken).ConfigureAwait(false);

        var targetHeaderKey = FirebirdSchemaHelper.PickColumn(targetHeaderCols, "DOCKEY", "ID");
        var targetDetailKey = FirebirdSchemaHelper.PickColumn(targetDetailCols, "DTLKEY", "ID");
        var targetDetailFk = FirebirdSchemaHelper.PickColumn(targetDetailCols, "DOCKEY");
        var sourceHeaderKey = FirebirdSchemaHelper.PickColumn(sourceHeaderCols, "DOCKEY", "ID");
        var sourceDetailKey = FirebirdSchemaHelper.PickColumn(sourceDetailCols, "DTLKEY", "ID");
        var sourceDetailFk = FirebirdSchemaHelper.PickColumn(sourceDetailCols, "DOCKEY");
        var xtransKey = FirebirdSchemaHelper.PickColumn(xtransCols, "DOCKEY", "ID");

        if (targetHeaderKey is null || targetDetailKey is null || targetDetailFk is null
            || sourceHeaderKey is null || sourceDetailKey is null || sourceDetailFk is null || xtransKey is null)
        {
            return GrTransferResult.Failure("Source or target document schema is missing required key columns.");
        }

        var targetHeaderStrLen = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, spec.TargetHeaderTable, cancellationToken).ConfigureAwait(false);
        var targetDetailStrLen = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, spec.TargetDetailTable, cancellationToken).ConfigureAwait(false);
        var xtransStrLen = await FirebirdTableWriter.GetStringColumnLengthsAsync(conn, "ST_XTRANS", cancellationToken).ConfigureAwait(false);

        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var sourceHeader = await FirebirdRowHelpers.LoadHeaderAsync(
                conn, tx, spec.SourceHeaderTable, sourceHeaderKey, sourceDocKey, cancellationToken).ConfigureAwait(false);
            if (sourceHeader is null)
                return GrTransferResult.Failure($"Source document {sourceDocNo} was not found.");

            var sourceLines = await FirebirdRowHelpers.LoadDetailsAsync(
                conn, tx, spec.SourceDetailTable, sourceDetailFk, sourceDocKey, cancellationToken).ConfigureAwait(false);
            if (sourceLines.Count == 0)
                return GrTransferResult.Failure("Source document has no detail lines to transfer.");

            var detailIds = sourceLines
                .Select(l => FirebirdRowHelpers.GetInt(l, sourceDetailKey))
                .Where(id => id > 0)
                .ToList();

            var existingQty = await XTransQtyQuery.FetchExistingTransferQtyMapAsync(
                conn, tx, sourceDocKey, spec.FromDocType, detailIds, xtransCols, cancellationToken, spec.ToDocType)
                .ConfigureAwait(false);

            var partialPlan = PartialTransferPlan.FromLines(lines);
            var transfers = new List<(Dictionary<string, object?> Source, int SourceDtlKey, decimal Qty)>();
            foreach (var line in sourceLines)
            {
                var dtlKey = FirebirdRowHelpers.GetInt(line, sourceDetailKey);
                if (dtlKey <= 0) continue;
                if (!FirebirdRowHelpers.IsTransferable(line)) continue;

                var sourceQty = FirebirdRowHelpers.LineSourceQty(line);
                if (sourceQty <= 0) continue;

                var already = existingQty.GetValueOrDefault(dtlKey);
                var remaining = FirebirdRowHelpers.Money(sourceQty - already);
                if (remaining <= 0) continue;

                decimal transferQty;
                if (partialPlan is not null)
                {
                    var itemCode = FirebirdRowHelpers.Clean(line.GetValueOrDefault("ITEMCODE")) ?? "";
                    var seq = FirebirdRowHelpers.GetInt(line, "SEQ");
                    if (!partialPlan.TryTakeLine(seq, itemCode, remaining, out transferQty))
                        continue;
                }
                else
                {
                    transferQty = remaining;
                }

                transfers.Add((line, dtlKey, transferQty));
            }

            if (transfers.Count == 0)
            {
                return partialPlan is not null
                    ? GrTransferResult.Failure("No scanned items matched transferable lines with remaining quantity.")
                    : GrTransferResult.Failure("No outstanding quantity remains to transfer (or lines are not transferable).");
            }

            var docno = await NextDocNoAsync(conn, tx, spec, targetHeaderCols, cancellationToken).ConfigureAwait(false);
            if (await DocNoExistsAsync(conn, tx, spec.TargetHeaderTable, targetHeaderCols, docno, cancellationToken).ConfigureAwait(false))
                return GrTransferResult.Failure($"Document number already exists: {docno}");

            var targetDocKey = await FirebirdTableWriter.NextKeyAsync(
                conn, spec.TargetHeaderTable, targetHeaderKey,
                spec.TargetHeaderGenerators,
                cancellationToken, tx).ConfigureAwait(false);

            var effectiveDate = FirebirdRowHelpers.NormalizeDate(sourceHeader.GetValueOrDefault("DOCDATE")) ?? DateTime.UtcNow.Date;
            var sourceNo = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DOCNO")) ?? sourceDocNo;
            var partyCode = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("CODE")) ?? "";
            var statusCol = FirebirdSchemaHelper.PickColumn(targetHeaderCols, "STATUS");
            var targetStatus = 0;

            decimal totalDocAmount = 0;
            var linePayloads = new List<(Dictionary<string, object?> Detail, Dictionary<string, object?> XTrans)>();
            var lineIndex = 0;

            foreach (var (source, sourceDtlKey, qty) in transfers)
            {
                lineIndex++;
                var unitPrice = FirebirdRowHelpers.Money(FirebirdRowHelpers.ToDecimal(source.GetValueOrDefault("UNITPRICE")));
                var sourceQty = FirebirdRowHelpers.LineSourceQty(source);
                var lineTax = FirebirdRowHelpers.Money(FirebirdRowHelpers.ToDecimal(source.GetValueOrDefault("TAXAMT")));
                if (sourceQty > 0 && lineTax != 0)
                    lineTax = FirebirdRowHelpers.Money((lineTax / sourceQty) * qty);
                else
                    lineTax = 0;

                var amount = FirebirdRowHelpers.Money((unitPrice * qty) + lineTax);
                totalDocAmount += amount;

                var lineSeq = source.GetValueOrDefault("SEQ") ?? lineIndex * 1000;

                var detailValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [targetDetailFk] = targetDocKey,
                    ["SEQ"] = lineSeq,
                    ["ITEMCODE"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("ITEMCODE")),
                    ["LOCATION"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("LOCATION")),
                    ["BATCH"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("BATCH")),
                    ["PROJECT"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("PROJECT"))
                                  ?? FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("PROJECT"))
                                  ?? "----",
                    ["DESCRIPTION"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("DESCRIPTION")),
                    ["DESCRIPTION2"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("DESCRIPTION2")),
                    ["DESCRIPTION3"] = source.GetValueOrDefault("DESCRIPTION3"),
                    ["PERMITNO"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("PERMITNO")),
                    ["QTY"] = (double)qty,
                    ["UOM"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("UOM")) ?? "UNIT",
                    ["RATE"] = (double)FirebirdRowHelpers.ToDecimal(source.GetValueOrDefault("RATE"), 1m),
                    ["SQTY"] = (double)qty,
                    ["SUOMQTY"] = (double)(FirebirdRowHelpers.Money(FirebirdRowHelpers.ToDecimal(source.GetValueOrDefault("SUOMQTY"))) > 0
                        ? FirebirdRowHelpers.ToDecimal(source.GetValueOrDefault("SUOMQTY"))
                        : qty),
                    ["OFFSETQTY"] = (double)FirebirdRowHelpers.ToDecimal(source.GetValueOrDefault("OFFSETQTY")),
                    ["UNITPRICE"] = (double)unitPrice,
                    ["DELIVERYDATE"] = FirebirdRowHelpers.NormalizeDate(source.GetValueOrDefault("DELIVERYDATE")) ?? effectiveDate,
                    ["DISC"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("DISC")),
                    ["TAX"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("TAX")),
                    ["TARIFF"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("TARIFF")),
                    ["TAXEXEMPTIONREASON"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("TAXEXEMPTIONREASON")),
                    ["IRBM_CLASSIFICATION"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("IRBM_CLASSIFICATION")),
                    ["TAXRATE"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("TAXRATE")),
                    ["TAXAMT"] = (double)lineTax,
                    ["LOCALTAXAMT"] = (double)lineTax,
                    ["EXEMPTED_TAXRATE"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("EXEMPTED_TAXRATE")),
                    ["EXEMPTED_TAXAMT"] = (double)FirebirdRowHelpers.ToDecimal(source.GetValueOrDefault("EXEMPTED_TAXAMT")),
                    ["TAXINCLUSIVE"] = FirebirdRowHelpers.CoerceBool(source.GetValueOrDefault("TAXINCLUSIVE")),
                    ["AMOUNT"] = (double)amount,
                    ["LOCALAMOUNT"] = (double)amount,
                    ["PRINTABLE"] = source.GetValueOrDefault("PRINTABLE") is null
                        || FirebirdRowHelpers.CoerceBool(source.GetValueOrDefault("PRINTABLE")),
                    ["FROMDOCTYPE"] = spec.FromDocType,
                    ["FROMDOCKEY"] = sourceDocKey,
                    ["FROMDTLKEY"] = sourceDtlKey,
                    ["TRANSFERABLE"] = true,
                    ["REMARK1"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("REMARK1")),
                    ["REMARK2"] = FirebirdRowHelpers.Clean(source.GetValueOrDefault("REMARK2")),
                };

                foreach (var extraCol in spec.ExtraTargetDetailQtyColumns)
                {
                    if (targetDetailCols.Contains(extraCol))
                        detailValues[extraCol] = (double)qty;
                }

                var xRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CODE"] = partyCode,
                    ["FROMDOCTYPE"] = spec.FromDocType,
                    ["TODOCTYPE"] = spec.ToDocType,
                    ["FROMDOCKEY"] = sourceDocKey,
                    ["TODOCKEY"] = targetDocKey,
                    ["FROMDTLKEY"] = sourceDtlKey,
                    ["QTY"] = (double)qty,
                    ["SQTY"] = (double)qty,
                };
                if (xtransCols.Contains("SUOMQTY"))
                    xRow["SUOMQTY"] = (double)qty;
                if (statusCol is not null && xtransCols.Contains("TOSTATUS"))
                    xRow["TOSTATUS"] = targetStatus;

                linePayloads.Add((detailValues, xRow));
            }

            var description = spec.DescriptionTemplate.Replace("{DOCNO}", sourceNo, StringComparison.OrdinalIgnoreCase);

            var headerValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [targetHeaderKey] = targetDocKey,
                ["DOCNO"] = docno,
                ["DOCNOEX"] = docno,
                ["DOCDATE"] = effectiveDate,
                ["POSTDATE"] = effectiveDate,
                ["TAXDATE"] = effectiveDate,
                ["CODE"] = partyCode,
                ["COMPANYNAME"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("COMPANYNAME")),
                ["ADDRESS1"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("ADDRESS1")),
                ["ADDRESS2"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("ADDRESS2")),
                ["ADDRESS3"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("ADDRESS3")),
                ["ADDRESS4"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("ADDRESS4")),
                ["POSTCODE"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("POSTCODE")),
                ["CITY"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("CITY")),
                ["STATE"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("STATE")),
                ["COUNTRY"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("COUNTRY")),
                ["PHONE1"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("PHONE1")),
                ["MOBILE"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("MOBILE")),
                ["FAX1"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("FAX1")),
                ["ATTENTION"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("ATTENTION")),
                ["AREA"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("AREA")) ?? "----",
                ["AGENT"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("AGENT")) ?? partyCode,
                ["PROJECT"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("PROJECT")) ?? "----",
                ["TERMS"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("TERMS")),
                ["CURRENCYCODE"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("CURRENCYCODE")) ?? "MYR",
                ["CURRENCYRATE"] = (double)FirebirdRowHelpers.ToDecimal(sourceHeader.GetValueOrDefault("CURRENCYRATE"), 1m),
                ["SHIPPER"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("SHIPPER")) ?? "----",
                ["DESCRIPTION"] = description,
                ["CANCELLED"] = false,
                ["STATUS"] = targetStatus,
                ["DOCAMT"] = (double)FirebirdRowHelpers.Money(totalDocAmount),
                ["LOCALDOCAMT"] = (double)FirebirdRowHelpers.Money(totalDocAmount),
                ["BRANCHNAME"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("BRANCHNAME")),
                ["DADDRESS1"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DADDRESS1")),
                ["DADDRESS2"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DADDRESS2")),
                ["DADDRESS3"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DADDRESS3")),
                ["DADDRESS4"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DADDRESS4")),
                ["DPOSTCODE"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DPOSTCODE")),
                ["DCITY"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DCITY")),
                ["DSTATE"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DSTATE")),
                ["DCOUNTRY"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DCOUNTRY")),
                ["DATTENTION"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DATTENTION")),
                ["DPHONE1"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DPHONE1")),
                ["DMOBILE"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DMOBILE")),
                ["DFAX1"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("DFAX1")),
                ["TAXEXEMPTNO"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("TAXEXEMPTNO")),
                ["SALESTAXNO"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("SALESTAXNO")),
                ["SERVICETAXNO"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("SERVICETAXNO")),
                ["TIN"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("TIN")),
                ["IDTYPE"] = (int)FirebirdRowHelpers.ToDecimal(sourceHeader.GetValueOrDefault("IDTYPE")),
                ["IDNO"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("IDNO")),
                ["TOURISMNO"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("TOURISMNO")),
                ["SIC"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("SIC")),
                ["INCOTERMS"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("INCOTERMS")),
                ["SUBMISSIONTYPE"] = (int)FirebirdRowHelpers.ToDecimal(sourceHeader.GetValueOrDefault("SUBMISSIONTYPE")),
                ["BUSINESSUNIT"] = FirebirdRowHelpers.Clean(sourceHeader.GetValueOrDefault("BUSINESSUNIT")),
                ["TRANSFERABLE"] = true,
                ["UPDATECOUNT"] = 0,
                ["PRINTCOUNT"] = 0,
                ["LASTMODIFIED"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            if (targetHeaderCols.Contains("FROMDOCKEY"))
                headerValues["FROMDOCKEY"] = sourceDocKey;
            if (targetHeaderCols.Contains("FROMDOCTYPE"))
                headerValues["FROMDOCTYPE"] = spec.FromDocType;

            await FirebirdTableWriter.InsertDynamicAsync(
                conn, spec.TargetHeaderTable,
                FirebirdTableWriter.FitStrings(headerValues, targetHeaderStrLen),
                targetHeaderCols, cancellationToken, tx).ConfigureAwait(false);

            var nextDtlKey = await FirebirdTableWriter.NextGlobalDtlKeyAsync(
                conn, targetDetailKey, cancellationToken, tx).ConfigureAwait(false);

            foreach (var (detailRow, xRow) in linePayloads)
            {
                while (await FirebirdTableWriter.GlobalDtlKeyExistsAsync(
                           conn, targetDetailKey, nextDtlKey, tx, cancellationToken).ConfigureAwait(false))
                    nextDtlKey++;

                detailRow[targetDetailKey] = nextDtlKey;

                await FirebirdTableWriter.InsertDynamicAsync(
                    conn, spec.TargetDetailTable,
                    FirebirdTableWriter.FitStrings(detailRow, targetDetailStrLen),
                    targetDetailCols, cancellationToken, tx).ConfigureAwait(false);

                xRow[xtransKey] = await FirebirdTableWriter.NextKeyAsync(
                    conn, "ST_XTRANS", xtransKey,
                    spec.XTransGenerators,
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
                "Firebird transfer OK: {From} {Src} (docKey {DocKey}) -> {To} {Target} ({LineCount} lines)",
                spec.FromDocType, sourceDocNo, sourceDocKey, spec.ToDocType, docno, linePayloads.Count);

            return GrTransferResult.Success(docno);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(cancellationToken).ConfigureAwait(false); } catch { /* ignore */ }
            _logger.LogWarning(ex, "Firebird {From}->{To} transfer failed for docKey {DocKey}.",
                spec.FromDocType, spec.ToDocType, sourceDocKey);
            return GrTransferResult.Failure(ex.Message);
        }
    }

    private static async Task<string> NextDocNoAsync(
        FbConnection conn,
        FbTransaction tx,
        DocumentTransferSpec spec,
        HashSet<string> targetHeaderCols,
        CancellationToken cancellationToken)
    {
        var docnoCol = FirebirdSchemaHelper.PickColumn(targetHeaderCols, "DOCNO");
        if (docnoCol is null)
            return DocNumbering.BuildNextDocNo(0, DocNumbering.DefaultPad, spec.DocNoPrefix);

        var like = spec.DocNoPrefix + "%";
        var sql = $"SELECT {docnoCol} FROM {spec.TargetHeaderTable} WHERE {docnoCol} LIKE @P";
        var docnos = new List<string?>();
        await using (var cmd = new FbCommand(sql, conn, tx))
        {
            cmd.Parameters.Add("@P", FbDbType.VarChar).Value = like;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue;
                docnos.Add(reader.GetString(0)?.Trim());
            }
        }

        var (max, pad, prefix) = DocNumbering.SeedFromStrings(docnos, spec.DocNoPrefix);
        return DocNumbering.BuildNextDocNo(max, pad, prefix);
    }

    private static async Task<bool> DocNoExistsAsync(
        FbConnection conn,
        FbTransaction tx,
        string table,
        HashSet<string> headerCols,
        string docno,
        CancellationToken cancellationToken)
    {
        var docnoCol = FirebirdSchemaHelper.PickColumn(headerCols, "DOCNO");
        if (docnoCol is null || string.IsNullOrWhiteSpace(docno))
            return false;

        var sql = $"SELECT FIRST 1 {docnoCol} FROM {table} WHERE {docnoCol} = @N";
        await using var cmd = new FbCommand(sql, conn, tx);
        cmd.Parameters.Add("@N", FbDbType.VarChar).Value = docno.Trim();
        var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return obj is not null and not DBNull;
    }

    private sealed class PartialTransferPlan
    {
        public Dictionary<int, decimal> ByLineNo { get; } = new();
        public Dictionary<string, decimal> ByItemCode { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static PartialTransferPlan? FromLines(IReadOnlyList<GrTransferLineRequest>? lines)
        {
            if (lines is null || lines.Count == 0)
                return null;

            var plan = new PartialTransferPlan();
            foreach (var line in lines)
            {
                var code = (line.ItemCode ?? "").Trim();
                if (code.Length == 0 || line.Quantity <= 0) continue;

                if (line.PoLineNo is int poLine && poLine > 0)
                {
                    if (plan.ByLineNo.TryGetValue(poLine, out var existingLine))
                        plan.ByLineNo[poLine] = FirebirdRowHelpers.Money(existingLine + line.Quantity);
                    else
                        plan.ByLineNo[poLine] = FirebirdRowHelpers.Money(line.Quantity);
                    continue;
                }

                if (plan.ByItemCode.TryGetValue(code, out var existingCode))
                    plan.ByItemCode[code] = FirebirdRowHelpers.Money(existingCode + line.Quantity);
                else
                    plan.ByItemCode[code] = FirebirdRowHelpers.Money(line.Quantity);
            }

            return plan.ByLineNo.Count + plan.ByItemCode.Count > 0 ? plan : null;
        }

        public bool TryTakeLine(int seq, string itemCode, decimal remaining, out decimal transferQty)
        {
            transferQty = 0;
            if (remaining <= 0) return false;

            if (seq > 0 && ByLineNo.TryGetValue(seq, out var byLine) && byLine > 0)
            {
                transferQty = FirebirdRowHelpers.Money(Math.Min(byLine, remaining));
                if (transferQty <= 0) return false;
                var left = byLine - transferQty;
                if (left <= 0) ByLineNo.Remove(seq);
                else ByLineNo[seq] = left;
                return true;
            }

            if (ByLineNo.Count > 0)
                return false;

            if (string.IsNullOrEmpty(itemCode) || !ByItemCode.TryGetValue(itemCode, out var byCode) || byCode <= 0)
                return false;

            transferQty = FirebirdRowHelpers.Money(Math.Min(byCode, remaining));
            if (transferQty <= 0) return false;
            var codeLeft = byCode - transferQty;
            if (codeLeft <= 0) ByItemCode.Remove(itemCode);
            else ByItemCode[itemCode] = codeLeft;
            return true;
        }
    }
}
