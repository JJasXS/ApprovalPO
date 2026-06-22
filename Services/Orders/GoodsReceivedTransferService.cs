using System.Globalization;
using System.Text.Json;
using ApprovalPO.Helpers;
using ApprovalPO.Services.SqlApi;

namespace ApprovalPO.Services.Orders;

/// <summary>Outcome of a PO -> Goods Received transfer attempt.</summary>
public sealed record GrTransferResult(bool Ok, bool ApiAvailable, string? GrDocNo, string? Error)
{
    public static GrTransferResult Unavailable(string? msg = null) => new(false, false, null, msg);
    public static GrTransferResult Success(string grDocNo) => new(true, true, grDocNo, null);
    public static GrTransferResult Failure(string error) => new(false, true, null, error);
}

public interface IGoodsReceivedTransfer
{
    /// <summary>True when the tenant can post Goods Received via the SQL API.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers PO lines to a new Goods Received document.
    /// When <paramref name="lines"/> is provided, only those item codes and quantities are transferred (capped to outstanding balance).
    /// When null or empty, transfers full outstanding balance on all transferable lines.
    /// </summary>
    Task<GrTransferResult> TransferPoAsync(
        int docKey,
        string poNumber,
        IReadOnlyList<GrTransferLineRequest>? lines = null,
        CancellationToken cancellationToken = default);
}

public sealed class GoodsReceivedTransferService : IGoodsReceivedTransfer
{
    private readonly PoToGoodsReceiptFirebirdTransferService _firebird;
    private readonly ISqlAccountingApi _api;
    private readonly ILogger<GoodsReceivedTransferService> _logger;

    public GoodsReceivedTransferService(
        PoToGoodsReceiptFirebirdTransferService firebird,
        ISqlAccountingApi api,
        ILogger<GoodsReceivedTransferService> logger)
    {
        _firebird = firebird;
        _api = api;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        await _api.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

    public async Task<GrTransferResult> TransferPoAsync(
        int docKey,
        string poNumber,
        IReadOnlyList<GrTransferLineRequest>? lines = null,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return GrTransferResult.Failure("Invalid PO document key.");

        try
        {
            var firebird = await _firebird.TransferAsync(docKey, poNumber, lines, cancellationToken).ConfigureAwait(false);
            if (firebird.Ok)
                return firebird;

            _logger.LogInformation(
                "Firebird PO->GR transfer for {Po} (docKey {DocKey}) did not complete: {Error}. Trying SQL API.",
                poNumber, docKey, firebird.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebird PO->GR transfer threw for {Po}; trying SQL API.", poNumber);
        }

        return await TransferViaSqlApiAsync(docKey, poNumber, lines, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GrTransferResult> TransferViaSqlApiAsync(
        int docKey,
        string poNumber,
        IReadOnlyList<GrTransferLineRequest>? lines,
        CancellationToken cancellationToken)
    {
        if (!await _api.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            return GrTransferResult.Unavailable();

        var poResp = await _api.SendAsync(HttpMethod.Get, $"/purchaseorder/{docKey}", null, cancellationToken).ConfigureAwait(false);
        if (!poResp.Available)
            return GrTransferResult.Unavailable();
        if (!poResp.IsSuccess)
            return GrTransferResult.Failure($"Could not load PO from SQL ({poResp.Status}). {ExtractError(poResp.Body)}");

        if (!TryReadPo(poResp.Body, out var po))
            return GrTransferResult.Failure("PO has no detail lines to transfer.");

        var transferLines = SelectPoLinesForTransfer(po, lines);
        if (transferLines.Count == 0)
            return GrTransferResult.Failure("No matching PO lines to transfer for the scanned items.");

        var (startSeq, pad, prefix) = await NextGrDocNoSeedAsync(cancellationToken).ConfigureAwait(false);

        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var seq = startSeq; seq < startSeq + 200; seq++)
        {
            var grDocNo = $"{prefix}{seq.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0')}";
            var payload = BuildPayload(po with { Lines = transferLines }, grDocNo, today);
            var resp = await _api.SendAsync(HttpMethod.Post, "/goodsreceived", payload, cancellationToken).ConfigureAwait(false);

            if (resp.IsSuccess)
            {
                var created = ReadDocNo(resp.Body) ?? grDocNo;
                _logger.LogInformation("SQL API GR transfer OK: PO {Po} (docKey {DocKey}) -> GR {Gr}", poNumber, docKey, created);
                return GrTransferResult.Success(created);
            }

            var lower = (resp.Body ?? "").ToLowerInvariant();
            if (lower.Contains("unique") && lower.Contains("document"))
                continue;

            return GrTransferResult.Failure(ExtractError(resp.Body) ?? $"SQL rejected the transfer ({resp.Status}).");
        }

        return GrTransferResult.Failure("Could not allocate a unique Goods Received number.");
    }

    private sealed record PoLine(long DtlKey, int Seq, string ItemCode, string Location, string Batch, string Description, string Qty, string Uom, string UnitPrice, string Irbm);

    private sealed record PoDoc(long DocKey, string Code, string CompanyName, string CurrencyCode, string CurrencyRate, string Terms, string BranchName, string Tin, string Sic, List<PoLine> Lines);

    private static bool TryReadPo(string body, out PoDoc po)
    {
        po = new PoDoc(0, "", "", "", "1", "", "", "", "", new List<PoLine>());
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement rec;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                if (data.GetArrayLength() == 0) return false;
                rec = data[0];
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return false;
                rec = root[0];
            }
            else
            {
                rec = root;
            }

            var lines = new List<PoLine>();
            if (rec.TryGetProperty("sdsdocdetail", out var detail) && detail.ValueKind == JsonValueKind.Array)
            {
                foreach (var ln in detail.EnumerateArray())
                {
                    var qty = Str(ln, "qty");
                    if (string.IsNullOrWhiteSpace(qty) || qty == "0")
                        qty = Str(ln, "sqty");
                    lines.Add(new PoLine(
                        DtlKey: Num(ln, "dtlkey"),
                        Seq: (int)Num(ln, "seq"),
                        ItemCode: Str(ln, "itemcode"),
                        Location: Blank(Str(ln, "location"), "----"),
                        Batch: Str(ln, "batch"),
                        Description: Str(ln, "description"),
                        Qty: string.IsNullOrWhiteSpace(qty) ? "0" : qty,
                        Uom: Str(ln, "uom"),
                        UnitPrice: Blank(Str(ln, "unitprice"), "0"),
                        Irbm: Str(ln, "irbm_classification")));
                }
            }

            if (lines.Count == 0)
                return false;

            po = new PoDoc(
                DocKey: Num(rec, "dockey"),
                Code: Str(rec, "code"),
                CompanyName: Str(rec, "companyname"),
                CurrencyCode: Blank(Str(rec, "currencycode"), "----"),
                CurrencyRate: Blank(Str(rec, "currencyrate"), "1"),
                Terms: Str(rec, "terms"),
                BranchName: Str(rec, "branchname"),
                Tin: Str(rec, "tin"),
                Sic: Str(rec, "sic"),
                Lines: lines);
            return po.DocKey > 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<PoLine> SelectPoLinesForTransfer(PoDoc po, IReadOnlyList<GrTransferLineRequest>? lines)
    {
        if (lines is null || lines.Count == 0)
            return po.Lines;

        var byCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var code = (line.ItemCode ?? "").Trim();
            if (code.Length == 0 || line.Quantity <= 0) continue;
            byCode[code] = byCode.TryGetValue(code, out var existing) ? existing + line.Quantity : line.Quantity;
        }

        var result = new List<PoLine>();
        foreach (var poLine in po.Lines)
        {
            if (!byCode.TryGetValue(poLine.ItemCode, out var requested)) continue;
            var poQty = decimal.TryParse(poLine.Qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var pq)
                ? pq
                : 0m;
            var transferQty = poQty > 0 ? Math.Min(requested, poQty) : requested;
            if (transferQty <= 0) continue;
            result.Add(poLine with { Qty = transferQty.ToString(CultureInfo.InvariantCulture) });
        }

        return result;
    }

    private static string BuildPayload(PoDoc po, string grDocNo, string today)
    {
        var detail = po.Lines.Select(l => new Dictionary<string, object?>
        {
            ["dtlkey"] = -1,
            ["seq"] = l.Seq,
            ["itemcode"] = l.ItemCode,
            ["location"] = l.Location,
            ["batch"] = l.Batch,
            ["project"] = "----",
            ["description"] = l.Description,
            ["qty"] = l.Qty,
            ["uom"] = l.Uom,
            ["unitprice"] = l.UnitPrice,
            ["irbm_classification"] = l.Irbm,
            ["deliverydate"] = today,
            // Transfer linkage back to the source PO line:
            ["fromdoctype"] = SqlAccountingDocTypes.PurchaseOrder,
            ["fromdockey"] = po.DocKey,
            ["fromdtlkey"] = l.DtlKey,
            ["transferable"] = true,
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["dockey"] = 0,
            ["docno"] = grDocNo,
            ["docdate"] = today,
            ["postdate"] = today,
            ["taxdate"] = today,
            ["code"] = po.Code,
            ["companyname"] = po.CompanyName,
            ["area"] = "----",
            ["agent"] = "----",
            ["project"] = "----",
            ["terms"] = po.Terms,
            ["shipper"] = "----",
            ["currencycode"] = po.CurrencyCode,
            ["currencyrate"] = po.CurrencyRate,
            ["branchname"] = po.BranchName,
            ["tin"] = po.Tin,
            ["sic"] = po.Sic,
            ["description"] = $"Goods Received from {ReadPoNo(po)} (ProAcc scan)",
            ["cancelled"] = false,
            ["status"] = 0,
            ["transferable"] = true,
            ["sdsdocdetail"] = detail,
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string ReadPoNo(PoDoc po) => po.Code; // header docno not tracked separately; description is informational only

    /// <summary>Scans existing GR docnos to seed the next number. Returns (startSeq, padWidth, prefix).</summary>
    private async Task<(int Start, int Pad, string Prefix)> NextGrDocNoSeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var resp = await _api.SendAsync(HttpMethod.Get, "/goodsreceived", null, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccess)
                return (1, GrNumbering.DefaultPad, GrNumbering.DefaultPrefix);

            using var doc = JsonDocument.Parse(resp.Body);
            var root = doc.RootElement;
            var rows = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                ? d
                : (root.ValueKind == JsonValueKind.Array ? root : default);

            var docnos = new List<string?>();
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rows.EnumerateArray())
                    docnos.Add(Str(r, "docno"));
            }

            var (max, pad, prefix) = GrNumbering.SeedFromStrings(docnos);
            return (max + 1, pad, prefix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not seed next GR number; starting from 1.");
            return (1, GrNumbering.DefaultPad, GrNumbering.DefaultPrefix);
        }
    }

    private static string? ReadDocNo(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("docno", out var dn) && dn.ValueKind == JsonValueKind.String)
            {
                var v = dn.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? ExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String)
            {
                return (msg.GetString() ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            }
        }
        catch { /* ignore */ }
        return body.Length > 300 ? body[..300] : body;
    }

    private static string Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static long Num(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static string Blank(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
