using System.Text.Json;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services;
using ApprovalPO.Services.Ocr;
using ApprovalPO.Services.Orders;
using ApprovalPO.Services.Scan;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[ValidateAntiForgeryToken]
public class ScanPODetailModel : PageModel
{
    private readonly IPurchaseOrderCatalog _orders;
    private readonly IPurchaseOrderScanQuery _scanQuery;
    private readonly IScanQrLinkResolver _scanResolver;
    private readonly IScanPoSubmitStore _scanSubmits;
    private readonly IOcrCaptureService _ocrCaptures;
    private readonly IOcrEmailSender _ocrEmail;
    private readonly IOpenAiVisionService _ocrVision;
    private readonly OcrScanEnrichmentService _ocrEnrichment;
    private readonly Services.Orders.IGoodsReceivedTransfer _grTransfer;
    private readonly ILogger<ScanPODetailModel> _logger;

    public ScanPODetailModel(
        IPurchaseOrderCatalog orders,
        IPurchaseOrderScanQuery scanQuery,
        IScanQrLinkResolver scanResolver,
        IScanPoSubmitStore scanSubmits,
        IOcrCaptureService ocrCaptures,
        IOcrEmailSender ocrEmail,
        IOpenAiVisionService ocrVision,
        OcrScanEnrichmentService ocrEnrichment,
        Services.Orders.IGoodsReceivedTransfer grTransfer,
        ILogger<ScanPODetailModel> logger)
    {
        _orders = orders;
        _scanQuery = scanQuery;
        _scanResolver = scanResolver;
        _scanSubmits = scanSubmits;
        _ocrCaptures = ocrCaptures;
        _ocrEmail = ocrEmail;
        _ocrVision = ocrVision;
        _ocrEnrichment = ocrEnrichment;
        _grTransfer = grTransfer;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public int DocKey { get; set; }

    public PurchaseOrderRow? Order { get; private set; }

    public IReadOnlyList<PurchaseRequestLineRow> Lines { get; private set; } = Array.Empty<PurchaseRequestLineRow>();

    public bool IsScanSubmitted { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (DocKey <= 0)
            return RedirectToPage("/ScanPO");

        Order = await _scanQuery.GetApprovedByDocKeyAsync(DocKey, cancellationToken).ConfigureAwait(false);

        if (Order is null)
            return Page();

        var state = await _scanSubmits.GetStateAsync(DocKey, cancellationToken).ConfigureAwait(false);
        IsScanSubmitted = state.IsSubmitted;
        ViewData["ScanSubmitted"] = IsScanSubmitted;

        Lines = ScanPoProjectHelper.EnrichDerivedProjects(
            await _orders.GetPurchaseRequestLinesAsync(DocKey, cancellationToken).ConfigureAwait(false));
        return Page();
    }

    public async Task<IActionResult> OnGetResolveScanAsync(
        string url,
        int docKey,
        [FromQuery] string[]? codes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> hints = codes ?? [];
        PurchaseOrderRow? order = null;
        IReadOnlyList<PurchaseRequestLineRow> poLines = [];

        if (docKey > 0)
        {
            order = await _scanQuery.GetApprovedByDocKeyAsync(docKey, cancellationToken).ConfigureAwait(false);
            if (order is null)
            {
                return new JsonResult(new ScanQrResolveResult
                {
                    Scanned = url ?? "",
                    Error = "Purchase order not found or not approved.",
                    ErrorCode = "po_not_found"
                });
            }

            poLines = ScanPoProjectHelper.EnrichDerivedProjects(
                await _orders.GetPurchaseRequestLinesAsync(docKey, cancellationToken).ConfigureAwait(false));
            hints = poLines
                .Select(l => (l.ItemCode ?? "").Trim())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var result = await _scanResolver.ResolveAsync(url ?? "", hints, cancellationToken).ConfigureAwait(false);
        if (docKey <= 0 || order is null)
            return new JsonResult(result);

        var itemCode = (result.ItemCode ?? "").Trim();
        if (string.IsNullOrEmpty(itemCode))
            return new JsonResult(result);

        var scanProject = result.ScanLocation ?? "";
        var matchedRow = ScanPoValidationHelper.MatchPoLine(poLines, itemCode, scanProject);
        if (matchedRow is null)
        {
            var sameItem = poLines.Where(l => ScanPoValidationHelper.ItemCodesMatch(itemCode, l.ItemCode)).ToList();
            var errorCode = sameItem.Count > 1 && string.IsNullOrWhiteSpace(scanProject)
                ? "need_project"
                : "not_on_po";
            var error = errorCode switch
            {
                "need_project" =>
                    $"Item {itemCode} is on multiple project lines on {order.PoNumber}. Scan a label that includes the project (P1, P2, …).",
                _ when !string.IsNullOrWhiteSpace(scanProject) =>
                    $"No line for {itemCode} on project {scanProject.Trim()} on purchase order {order.PoNumber}.",
                _ => $"Item {itemCode} is not on purchase order {order.PoNumber}."
            };

            return new JsonResult(new ScanQrResolveResult
            {
                Scanned = result.Scanned,
                ItemCode = itemCode,
                ScanLocation = result.ScanLocation,
                Error = error,
                ErrorCode = errorCode,
                Source = result.Source,
                SearchedCodes = hints
            });
        }

        return new JsonResult(new ScanQrResolveResult
        {
            Scanned = result.Scanned,
            ItemCode = matchedRow.ItemCode,
            ScanQuantity = result.ScanQuantity,
            ScanLocation = matchedRow.Project,
            LineNo = matchedRow.LineNo,
            Source = result.Source,
            SearchedCodes = hints
        });
    }

    public async Task<IActionResult> OnGetScanStateAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new ScanPoSubmissionState(), ApprovalJson.CamelCase);

        var state = await _scanSubmits.GetStateAsync(docKey, cancellationToken).ConfigureAwait(false);
        return new JsonResult(state, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnPostSaveDraftAsync(int docKey, string? scanCountsJson, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false }, ApprovalJson.CamelCase);

        var order = await _scanQuery.GetApprovedByDocKeyAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new JsonResult(new { ok = false, error = "PO not found." }, ApprovalJson.CamelCase);

        var counts = TryParseScanCounts(scanCountsJson);
        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.SaveDraftAsync(docKey, order.PoNumber, counts, actor, cancellationToken).ConfigureAwait(false);
        return new JsonResult(new { ok = true }, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnPostSubmitAsync(int docKey, string? scanCountsJson, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return RedirectToPage("/ScanPO");

        var order = await _scanQuery.GetApprovedByDocKeyAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return RedirectToPage("/ScanPO");

        var counts = TryParseScanCounts(scanCountsJson);
        if (counts.Count == 0)
        {
            TempData["ScanSubmitError"] = "Enter received qty for at least one line before submitting.";
            return RedirectToPage("/ScanPODetail", new { docKey });
        }

        var poLines = await _orders.GetPurchaseRequestLinesAsync(docKey, cancellationToken).ConfigureAwait(false);
        var transferLines = TransferLinesFromScanCounts(counts, poLines);
        if (transferLines.Count == 0)
        {
            TempData["ScanSubmitError"] = "Received quantities could not be matched to this PO for Goods Received.";
            return RedirectToPage("/ScanPODetail", new { docKey });
        }

        var actor = ScanPoAuditHelper.FromUser(User);
        GrTransferOutcome outcome;
        try
        {
            var result = await _grTransfer.TransferPoAsync(docKey, order.PoNumber, transferLines, cancellationToken).ConfigureAwait(false);
            outcome = result.ApiAvailable
                ? (result.Ok
                    ? GrTransferOutcome.Created(result.GrDocNo)
                    : GrTransferOutcome.Failed(result.Error))
                : GrTransferOutcome.Local;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GR transfer threw during submit; falling back to local record for {Po}.", order.PoNumber);
            outcome = GrTransferOutcome.Local;
        }

        if (outcome.Kind == GrTransferOutcomeKind.Failed)
        {
            TempData["ScanSubmitError"] = string.IsNullOrWhiteSpace(outcome.Error)
                ? "Goods Received transfer failed. Please try again or contact support."
                : $"Goods Received transfer failed: {outcome.Error}";
            return RedirectToPage("/ScanPODetail", new { docKey });
        }

        await _scanSubmits.MarkSubmittedAsync(docKey, order.PoNumber, counts, actor, cancellationToken).ConfigureAwait(false);

        var by = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Email : actor.DisplayName;
        TempData["ScanSubmitMessage"] = outcome.Kind == GrTransferOutcomeKind.Created
            ? $"Goods Received {outcome.GrDocNo} created from {order.PoNumber}."
            : $"Scan submitted for {order.PoNumber}. Goods Received API is not enabled for this company yet, so it was recorded locally.";
        return RedirectToPage("/ScanPO", new { tab = "submitted" });
    }

    public async Task<IActionResult> OnPostReopenAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return RedirectToPage("/ScanPO");

        var order = await _scanQuery.GetApprovedByDocKeyAsync(docKey, cancellationToken).ConfigureAwait(false);
        var poNumber = order?.PoNumber ?? "";

        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.ClearSubmissionAsync(docKey, poNumber, actor, cancellationToken).ConfigureAwait(false);

        var by = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Email : actor.DisplayName;
        TempData["ScanSubmitMessage"] = string.IsNullOrWhiteSpace(by)
            ? "PO reopened for scanning."
            : $"PO reopened for scanning by {by}.";
        return RedirectToPage("/ScanPODetail", new { docKey });
    }

    public async Task<IActionResult> OnPostResetScanAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false, error = "Invalid PO." }, ApprovalJson.CamelCase);

        var order = await _scanQuery.GetApprovedByDocKeyAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new JsonResult(new { ok = false, error = "PO not found." }, ApprovalJson.CamelCase);

        var state = await _scanSubmits.GetStateAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (state.IsSubmitted)
            return new JsonResult(new { ok = false, error = "PO is submitted. Reopen to scan again." }, ApprovalJson.CamelCase);

        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.ClearDraftAsync(docKey, order.PoNumber, actor, cancellationToken).ConfigureAwait(false);
        return new JsonResult(new { ok = true }, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnPostOcrCaptureAsync(int docKey, string? poNumber, string? ocrText, IFormFile? image, CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
            return new JsonResult(new { ok = false, error = "No image received." }, ApprovalJson.CamelCase);

        // Guard against oversized uploads (phone photos are typically 2-6 MB).
        const long maxBytes = 15L * 1024 * 1024;
        if (image.Length > maxBytes)
            return new JsonResult(new { ok = false, error = "Image too large (max 15 MB)." }, ApprovalJson.CamelCase);

        await using var stream = image.OpenReadStream();
        var result = await _ocrCaptures
            .SaveCaptureAsync(poNumber, docKey, stream, image.FileName, ocrText, cancellationToken)
            .ConfigureAwait(false);

        return new JsonResult(new
        {
            ok = result.Ok,
            url = result.Url,
            fileName = result.FileName,
            error = result.Error
        }, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnPostOcrEmailAsync(int docKey, string? poNumber, string? ocrText, IFormFile? image, CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        string? fileName = null;
        string? contentType = null;

        if (image is { Length: > 0 })
        {
            const long maxBytes = 15L * 1024 * 1024;
            if (image.Length > maxBytes)
                return new JsonResult(new { ok = false, error = "Image too large (max 15 MB)." }, ApprovalJson.CamelCase);

            using var ms = new MemoryStream();
            await image.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
            fileName = string.IsNullOrWhiteSpace(image.FileName) ? "ocr-capture.png" : image.FileName;
            contentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/png" : image.ContentType;
        }

        var po = string.IsNullOrWhiteSpace(poNumber) ? "PO" : poNumber.Trim();
        var subject = $"OCR scan - {po}";
        var body =
            $"OCR scan result for {po}\r\n" +
            $"Captured: {DateTime.Now:dd MMM yyyy HH:mm}\r\n\r\n" +
            "--- Recognized text ---\r\n" +
            (string.IsNullOrWhiteSpace(ocrText) ? "(No text detected)" : ocrText.Trim());

        var to = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.Identity?.Name
            ?? "";
        if (string.IsNullOrWhiteSpace(to))
            return new JsonResult(new { ok = false, error = "Your account has no email address for OCR delivery." }, ApprovalJson.CamelCase);

        var (sent, error) = await _ocrEmail
            .SendAsync(to.Trim(), subject, body, bytes, fileName, contentType, cancellationToken)
            .ConfigureAwait(false);

        return new JsonResult(new { ok = sent, to, error }, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnPostOcrAnalyzeAsync(int docKey, string? poNumber, IFormFile? image, CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
            return new JsonResult(new { ok = false, error = "No image received." }, ApprovalJson.CamelCase);

        const long maxBytes = 15L * 1024 * 1024;
        if (image.Length > maxBytes)
            return new JsonResult(new { ok = false, error = "Image too large (max 15 MB)." }, ApprovalJson.CamelCase);

        if (!_ocrVision.IsConfigured)
            return new JsonResult(new { ok = false, error = "AI OCR is not configured on the server." }, ApprovalJson.CamelCase);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await image.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        var contentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/png" : image.ContentType;
        var hint = await BuildOcrAnalyzeHintAsync(docKey, poNumber, cancellationToken).ConfigureAwait(false);

        var result = await _ocrVision.AnalyzeAsync(bytes, contentType, hint, cancellationToken).ConfigureAwait(false);
        if (result.Ok && result.Fields is not null)
            await _ocrEnrichment.EnrichAsync(result.Fields, cancellationToken).ConfigureAwait(false);

        if (result.Ok && docKey > 0)
        {
            try
            {
                var lines = await _orders.GetPurchaseRequestLinesAsync(docKey, cancellationToken).ConfigureAwait(false);
                var expected = string.Join(" || ", lines.Select(l =>
                    $"code='{(l.ItemCode ?? "").Trim()}' desc='{(l.Description ?? "").Trim()}' qty={(l.Sqty != 0 ? l.Sqty : l.Qty)}"));
                var scanned = string.Join(" || ", (result.Fields?.Items ?? new List<OcrLineItem>()).Select(i =>
                    $"code='{(i.Code ?? "").Trim()}' desc='{(i.Description ?? "").Trim()}' qty='{(i.Quantity ?? "").Trim()}'"));
                _logger.LogInformation("OCR compare PO {Po} docKey {DocKey}\n  EXPECTED (system): {Expected}\n  SCANNED  (AI):     {Scanned}",
                    poNumber, docKey, expected, scanned);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR compare diagnostic logging failed.");
            }
        }

        return new JsonResult(new
        {
            ok = result.Ok,
            cleanedText = result.CleanedText,
            fields = result.Fields,
            error = result.Error
        }, ApprovalJson.CamelCase);
    }

    /// <summary>
    /// Confirms the verified scan: transfers the PO to Goods Received in Firebird (PH_GR / ST_XTRANS),
    /// with SQL API as fallback when configured; otherwise records the scan locally.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmTransferAsync(
        int docKey,
        string? poNumber,
        string? scanCountsJson,
        string? transferLinesJson,
        CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false, error = "Invalid document." }, ApprovalJson.CamelCase);

        var order = await _scanQuery.GetApprovedByDocKeyAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new JsonResult(new { ok = false, error = "PO not found or not approved." }, ApprovalJson.CamelCase);

        var usedOcrTransferPayload = !string.IsNullOrWhiteSpace(transferLinesJson);
        var transferLines = TryParseTransferLines(transferLinesJson);
        if (transferLines.Count == 0 && !usedOcrTransferPayload)
        {
            var poLines = await _orders.GetPurchaseRequestLinesAsync(docKey, cancellationToken).ConfigureAwait(false);
            transferLines = TransferLinesFromScanCounts(TryParseScanCounts(scanCountsJson), poLines);
        }

        if (transferLines.Count == 0)
        {
            return new JsonResult(new
            {
                ok = false,
                error = "No scanned lines to transfer. Run OCR analyze and ensure item codes and quantities are detected."
            }, ApprovalJson.CamelCase);
        }

        transferLines = await ResolveTransferLinesAgainstPoAsync(docKey, transferLines, cancellationToken).ConfigureAwait(false);
        if (transferLines.Count == 0)
        {
            return new JsonResult(new
            {
                ok = false,
                error = "No scanned item codes matched this PO. Transfer only includes lines detected on the document with a matching item code."
            }, ApprovalJson.CamelCase);
        }

        var counts = ScanCountsFromTransferLines(transferLines, TryParseScanCounts(scanCountsJson));
        var actor = ScanPoAuditHelper.FromUser(User);

        GrTransferOutcome outcome;
        try
        {
            var result = await _grTransfer.TransferPoAsync(docKey, order.PoNumber, transferLines, cancellationToken).ConfigureAwait(false);
            outcome = result.ApiAvailable
                ? (result.Ok
                    ? GrTransferOutcome.Created(result.GrDocNo)
                    : GrTransferOutcome.Failed(result.Error))
                : GrTransferOutcome.Local;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GR transfer threw; falling back to local record for {Po}.", order.PoNumber);
            outcome = GrTransferOutcome.Local;
        }

        // A genuine SQL rejection (e.g. balance/validation) should be shown, not silently recorded.
        if (outcome.Kind == GrTransferOutcomeKind.Failed)
            return new JsonResult(new { ok = false, mode = "gr", error = outcome.Error ?? "Transfer was rejected by SQL." }, ApprovalJson.CamelCase);

        // Created in SQL, or recorded locally (no SQL API on this company): mark the scan submitted locally.
        await _scanSubmits.MarkSubmittedAsync(docKey, order.PoNumber, counts, actor, cancellationToken).ConfigureAwait(false);

        if (outcome.Kind == GrTransferOutcomeKind.Created)
        {
            return new JsonResult(new
            {
                ok = true,
                mode = "gr",
                grDocNo = outcome.GrDocNo,
                message = $"Goods Received {outcome.GrDocNo} created from {order.PoNumber}."
            }, ApprovalJson.CamelCase);
        }

        return new JsonResult(new
        {
            ok = true,
            mode = "local",
            message = $"Scan confirmed for {order.PoNumber}. (Goods Received API is not enabled for this company yet, so it was recorded locally.)"
        }, ApprovalJson.CamelCase);
    }

    private enum GrTransferOutcomeKind { Created, Local, Failed }

    private readonly record struct GrTransferOutcome(GrTransferOutcomeKind Kind, string? GrDocNo, string? Error)
    {
        public static GrTransferOutcome Created(string? grDocNo) => new(GrTransferOutcomeKind.Created, grDocNo, null);
        public static GrTransferOutcome Local => new(GrTransferOutcomeKind.Local, null, null);
        public static GrTransferOutcome Failed(string? error) => new(GrTransferOutcomeKind.Failed, null, error);
    }

    private async Task<string?> BuildOcrAnalyzeHintAsync(int docKey, string? poNumber, CancellationToken cancellationToken)
    {
        var po = (poNumber ?? "").Trim();
        var parts = new List<string>();
        if (po.Length > 0)
            parts.Add($"Expected purchase order number on the document: {po}.");

        if (docKey > 0)
        {
            var lines = await _orders.GetPurchaseRequestLinesAsync(docKey, cancellationToken).ConfigureAwait(false);
            var codes = lines
                .Select(l => (l.ItemCode ?? "").Trim())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (codes.Count > 0)
                parts.Add($"Expected line item codes on this PO: {string.Join(", ", codes)}.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private async Task<IReadOnlyList<Services.Orders.GrTransferLineRequest>> ResolveTransferLinesAgainstPoAsync(
        int docKey,
        IReadOnlyList<Services.Orders.GrTransferLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var poLines = await _orders.GetPurchaseRequestLinesAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (poLines.Count == 0)
            return lines;

        var byCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in poLines)
        {
            var code = (row.ItemCode ?? "").Trim();
            if (code.Length == 0) continue;
            byCode.TryAdd(code, code);
        }

        var merged = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var scanned = (line.ItemCode ?? "").Trim();
            if (scanned.Length == 0 || line.Quantity <= 0) continue;
            if (!byCode.TryGetValue(scanned, out var poCode)) continue;

            merged[poCode] = merged.TryGetValue(poCode, out var existing)
                ? existing + line.Quantity
                : line.Quantity;
        }

        return merged
            .Select(kv => new Services.Orders.GrTransferLineRequest(kv.Key, kv.Value))
            .ToList();
    }

    private static IReadOnlyList<Services.Orders.GrTransferLineRequest> TryParseTransferLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<Services.Orders.GrTransferLineRequest>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<TransferLineDto>>(json, ApprovalJson.CamelCase);
            if (rows is null || rows.Count == 0)
                return Array.Empty<Services.Orders.GrTransferLineRequest>();

            var list = new List<Services.Orders.GrTransferLineRequest>();
            foreach (var row in rows)
            {
                var code = (row.ItemCode ?? "").Trim();
                if (code.Length == 0) continue;
                var qty = row.Quantity;
                if (qty <= 0 && row.Qty > 0) qty = row.Qty;
                if (qty <= 0) continue;
                list.Add(new Services.Orders.GrTransferLineRequest(code, qty));
            }

            return list;
        }
        catch
        {
            return Array.Empty<Services.Orders.GrTransferLineRequest>();
        }
    }

    private static IReadOnlyList<Services.Orders.GrTransferLineRequest> TransferLinesFromScanCounts(
        IReadOnlyDictionary<string, int> scanCounts,
        IReadOnlyList<PurchaseRequestLineRow>? poLines = null)
    {
        if (scanCounts.Count == 0)
            return Array.Empty<Services.Orders.GrTransferLineRequest>();

        if (poLines is { Count: > 0 })
        {
            var fromLines = new List<Services.Orders.GrTransferLineRequest>();
            foreach (var line in poLines)
            {
                var key = ScanPoValidationHelper.LineScanKey(line.LineNo);
                if (!scanCounts.TryGetValue(key, out var n) || n <= 0)
                    continue;
                var code = (line.ItemCode ?? "").Trim();
                if (code.Length == 0)
                    continue;
                var poQty = line.Sqty != 0 ? line.Sqty : line.Qty;
                var received = (decimal)n;
                if (poQty > 0)
                    received = Math.Min(received, poQty);
                if (received <= 0)
                    continue;
                fromLines.Add(new Services.Orders.GrTransferLineRequest(code, received, line.LineNo));
            }

            if (fromLines.Count > 0)
                return fromLines;
        }

        return scanCounts
            .Where(kv =>
                !string.IsNullOrWhiteSpace(kv.Key) &&
                kv.Value > 0 &&
                !kv.Key.StartsWith("L:", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new Services.Orders.GrTransferLineRequest(kv.Key.Trim(), kv.Value))
            .ToList();
    }

    private static IReadOnlyDictionary<string, int> ScanCountsFromTransferLines(
        IReadOnlyList<Services.Orders.GrTransferLineRequest> transferLines,
        IReadOnlyDictionary<string, int> fallback)
    {
        if (transferLines.Count == 0)
            return fallback;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in transferLines)
        {
            var code = (line.ItemCode ?? "").Trim();
            if (code.Length == 0) continue;
            var n = (int)Math.Round(line.Quantity, MidpointRounding.AwayFromZero);
            if (n <= 0) continue;
            counts[code] = n;
        }

        return counts;
    }

    private sealed class TransferLineDto
    {
        public string? ItemCode { get; set; }
        public decimal Quantity { get; set; }
        public decimal Qty { get; set; }
    }

    private static IReadOnlyDictionary<string, int> TryParseScanCounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, int>();

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (raw is null || raw.Count == 0)
                return new Dictionary<string, int>();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, el) in raw)
            {
                if (string.IsNullOrWhiteSpace(code)) continue;
                var n = ParseScanCountElement(el);
                if (n > 0)
                    counts[code.Trim()] = n;
            }
            return counts;
        }
        catch
        {
            return new Dictionary<string, int>();
        }
    }

    private static int ParseScanCountElement(JsonElement el)
    {
        decimal d = el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDecimal(out var n) => n,
            JsonValueKind.String when decimal.TryParse(
                el.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => 0
        };
        if (d <= 0) return 0;
        return (int)Math.Round(d, MidpointRounding.AwayFromZero);
    }
}
