using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services;
using ApprovalPO.Services.Ocr;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class ScanPODetailModel : PageModel
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Test recipient for the "Send in email" OCR button.
    private const string OcrEmailRecipient = "jason.choo2004@gmail.com";

    private readonly IPurchaseOrderCatalog _orders;
    private readonly IScanQrLinkResolver _scanResolver;
    private readonly IScanPoSubmitStore _scanSubmits;
    private readonly IOcrCaptureService _ocrCaptures;
    private readonly IOcrEmailSender _ocrEmail;
    private readonly IOpenAiVisionService _ocrVision;
    private readonly Services.Orders.IGoodsReceivedTransfer _grTransfer;
    private readonly ILogger<ScanPODetailModel> _logger;

    public ScanPODetailModel(IPurchaseOrderCatalog orders, IScanQrLinkResolver scanResolver, IScanPoSubmitStore scanSubmits, IOcrCaptureService ocrCaptures, IOcrEmailSender ocrEmail, IOpenAiVisionService ocrVision, Services.Orders.IGoodsReceivedTransfer grTransfer, ILogger<ScanPODetailModel> logger)
    {
        _orders = orders;
        _scanResolver = scanResolver;
        _scanSubmits = scanSubmits;
        _ocrCaptures = ocrCaptures;
        _ocrEmail = ocrEmail;
        _ocrVision = ocrVision;
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

        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        Order = all.FirstOrDefault(o =>
            o.DocKey == DocKey &&
            string.Equals(o.Status, "Approved", StringComparison.OrdinalIgnoreCase));

        if (Order is null)
            return Page();

        var state = await _scanSubmits.GetStateAsync(DocKey, cancellationToken).ConfigureAwait(false);
        IsScanSubmitted = state.IsSubmitted;
        ViewData["ScanSubmitted"] = IsScanSubmitted;

        Lines = await _orders.GetPurchaseRequestLinesAsync(DocKey, cancellationToken).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnGetResolveScanAsync(string url, [FromQuery] string[]? codes, CancellationToken cancellationToken)
    {
        var result = await _scanResolver.ResolveAsync(url ?? "", codes, cancellationToken).ConfigureAwait(false);
        return new JsonResult(result);
    }

    public async Task<IActionResult> OnGetScanStateAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new ScanPoSubmissionState(), JsonCamel);

        var state = await _scanSubmits.GetStateAsync(docKey, cancellationToken).ConfigureAwait(false);
        return new JsonResult(state, JsonCamel);
    }

    public async Task<IActionResult> OnPostSaveDraftAsync(int docKey, string? scanCountsJson, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false }, JsonCamel);

        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var order = all.FirstOrDefault(o =>
            o.DocKey == docKey &&
            string.Equals(o.Status, "Approved", StringComparison.OrdinalIgnoreCase));

        if (order is null)
            return new JsonResult(new { ok = false, error = "PO not found." }, JsonCamel);

        var counts = TryParseScanCounts(scanCountsJson);
        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.SaveDraftAsync(docKey, order.PoNumber, counts, actor, cancellationToken).ConfigureAwait(false);
        return new JsonResult(new { ok = true }, JsonCamel);
    }

    public async Task<IActionResult> OnPostSubmitAsync(int docKey, string? scanCountsJson, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return RedirectToPage("/ScanPO");

        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var order = all.FirstOrDefault(o =>
            o.DocKey == docKey &&
            string.Equals(o.Status, "Approved", StringComparison.OrdinalIgnoreCase));

        if (order is null)
            return RedirectToPage("/ScanPO");

        var counts = TryParseScanCounts(scanCountsJson);
        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.MarkSubmittedAsync(docKey, order.PoNumber, counts, actor, cancellationToken).ConfigureAwait(false);

        var by = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Email : actor.DisplayName;
        TempData["ScanSubmitMessage"] = string.IsNullOrWhiteSpace(by)
            ? $"Scan submitted for {order.PoNumber}."
            : $"Scan submitted for {order.PoNumber} by {by}.";
        return RedirectToPage("/ScanPO", new { tab = "submitted" });
    }

    public async Task<IActionResult> OnPostReopenAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return RedirectToPage("/ScanPO");

        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var order = all.FirstOrDefault(o => o.DocKey == docKey);
        var poNumber = order?.PoNumber ?? "";

        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.ClearSubmissionAsync(docKey, poNumber, actor, cancellationToken).ConfigureAwait(false);

        var by = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Email : actor.DisplayName;
        TempData["ScanSubmitMessage"] = string.IsNullOrWhiteSpace(by)
            ? "PO reopened for scanning."
            : $"PO reopened for scanning by {by}.";
        return RedirectToPage("/ScanPODetail", new { docKey });
    }

    public async Task<IActionResult> OnPostOcrCaptureAsync(int docKey, string? poNumber, string? ocrText, IFormFile? image, CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
            return new JsonResult(new { ok = false, error = "No image received." }, JsonCamel);

        // Guard against oversized uploads (phone photos are typically 2-6 MB).
        const long maxBytes = 15L * 1024 * 1024;
        if (image.Length > maxBytes)
            return new JsonResult(new { ok = false, error = "Image too large (max 15 MB)." }, JsonCamel);

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
        }, JsonCamel);
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
                return new JsonResult(new { ok = false, error = "Image too large (max 15 MB)." }, JsonCamel);

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

        var (sent, error) = await _ocrEmail
            .SendAsync(OcrEmailRecipient, subject, body, bytes, fileName, contentType, cancellationToken)
            .ConfigureAwait(false);

        return new JsonResult(new { ok = sent, to = OcrEmailRecipient, error }, JsonCamel);
    }

    public async Task<IActionResult> OnPostOcrAnalyzeAsync(int docKey, string? poNumber, IFormFile? image, CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
            return new JsonResult(new { ok = false, error = "No image received." }, JsonCamel);

        const long maxBytes = 15L * 1024 * 1024;
        if (image.Length > maxBytes)
            return new JsonResult(new { ok = false, error = "Image too large (max 15 MB)." }, JsonCamel);

        if (!_ocrVision.IsConfigured)
            return new JsonResult(new { ok = false, error = "AI OCR is not configured on the server." }, JsonCamel);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await image.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        var contentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/png" : image.ContentType;
        var hint = await BuildOcrAnalyzeHintAsync(docKey, poNumber, cancellationToken).ConfigureAwait(false);

        var result = await _ocrVision.AnalyzeAsync(bytes, contentType, hint, cancellationToken).ConfigureAwait(false);

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
        }, JsonCamel);
    }

    /// <summary>
    /// Confirms the verified scan: transfers the PO to Goods Received in Firebird (PH_GR / ST_XTRANS),
    /// with SQL API as fallback when configured; otherwise records the scan locally.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmTransferAsync(int docKey, string? poNumber, string? scanCountsJson, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false, error = "Invalid document." }, JsonCamel);

        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var order = all.FirstOrDefault(o =>
            o.DocKey == docKey &&
            string.Equals(o.Status, "Approved", StringComparison.OrdinalIgnoreCase));
        if (order is null)
            return new JsonResult(new { ok = false, error = "PO not found or not approved." }, JsonCamel);

        var counts = TryParseScanCounts(scanCountsJson);
        var actor = ScanPoAuditHelper.FromUser(User);

        GrTransferOutcome outcome;
        try
        {
            var result = await _grTransfer.TransferPoAsync(docKey, order.PoNumber, cancellationToken).ConfigureAwait(false);
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
            return new JsonResult(new { ok = false, mode = "gr", error = outcome.Error ?? "Transfer was rejected by SQL." }, JsonCamel);

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
            }, JsonCamel);
        }

        return new JsonResult(new
        {
            ok = true,
            mode = "local",
            message = $"Scan confirmed for {order.PoNumber}. (Goods Received API is not enabled for this company yet, so it was recorded locally.)"
        }, JsonCamel);
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
                var n = el.ValueKind switch
                {
                    JsonValueKind.Number when el.TryGetInt32(out var i) => i,
                    JsonValueKind.String when int.TryParse(el.GetString(), out var parsed) => parsed,
                    _ => 0
                };
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
}
