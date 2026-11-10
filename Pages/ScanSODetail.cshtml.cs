using System.Text.Json;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Orders;
using ApprovalPO.Services.Scan;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[ValidateAntiForgeryToken]
public class ScanSODetailModel : PageModel
{
    private readonly ISalesOrderDeliveryQuery _delivery;
    private readonly IDeliveryOrderTransfer _doTransfer;
    private readonly IScanQrLinkResolver _scanResolver;
    private readonly IScanPoSubmitStore _scanSubmits;
    private readonly ILogger<ScanSODetailModel> _logger;

    public ScanSODetailModel(
        ISalesOrderDeliveryQuery delivery,
        IDeliveryOrderTransfer doTransfer,
        IScanQrLinkResolver scanResolver,
        IScanPoSubmitStore scanSubmits,
        ILogger<ScanSODetailModel> logger)
    {
        _delivery = delivery;
        _doTransfer = doTransfer;
        _scanResolver = scanResolver;
        _scanSubmits = scanSubmits;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public int DocKey { get; set; }

    public SalesOrderDeliveryListItem? Order { get; private set; }

    public IReadOnlyList<SalesOrderDeliveryLineRow> Lines { get; private set; } =
        Array.Empty<SalesOrderDeliveryLineRow>();

    public bool IsScanSubmitted { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (DocKey <= 0)
            return RedirectToPage("/ScanSO");

        Order = await _delivery.GetApprovedWithProgressAsync(DocKey, cancellationToken).ConfigureAwait(false);
        if (Order is null)
            return Page();

        var state = await _scanSubmits.GetStateAsync(DocKey, ScanDocKinds.SalesOrder, cancellationToken)
            .ConfigureAwait(false);
        IsScanSubmitted = state.IsSubmitted;
        Lines = await _delivery.GetLinesWithDeliveredAsync(DocKey, cancellationToken).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnGetResolveScanAsync(
        string url,
        int docKey,
        [FromQuery] string[]? codes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> hints = codes ?? [];
        SalesOrderDeliveryListItem? order = null;
        IReadOnlyList<SalesOrderDeliveryLineRow> soLines = [];

        if (docKey > 0)
        {
            order = await _delivery.GetApprovedWithProgressAsync(docKey, cancellationToken).ConfigureAwait(false);
            if (order is null)
            {
                return new JsonResult(new ScanQrResolveResult
                {
                    Scanned = url ?? "",
                    Error = "Sales order not found or not approved.",
                    ErrorCode = "po_not_found"
                });
            }

            soLines = await _delivery.GetLinesWithDeliveredAsync(docKey, cancellationToken).ConfigureAwait(false);
            hints = soLines
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
        var matchLines = ToMatchRows(soLines);
        var matchedRow = ScanPoValidationHelper.MatchPoLine(matchLines, itemCode, scanProject);
        if (matchedRow is null)
        {
            var sameItem = soLines.Where(l => ScanPoValidationHelper.ItemCodesMatch(itemCode, l.ItemCode)).ToList();
            var errorCode = sameItem.Count > 1 && string.IsNullOrWhiteSpace(scanProject)
                ? "need_project"
                : "not_on_po";
            var error = errorCode switch
            {
                "need_project" =>
                    $"Item {itemCode} is on multiple project lines on {order.SoNumber}. Scan a label that includes the project (P1, P2, …).",
                _ when !string.IsNullOrWhiteSpace(scanProject) =>
                    $"No line for {itemCode} on project {scanProject.Trim()} on sales order {order.SoNumber}.",
                _ => $"Item {itemCode} is not on sales order {order.SoNumber}."
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

        var state = await _scanSubmits.GetStateAsync(docKey, ScanDocKinds.SalesOrder, cancellationToken)
            .ConfigureAwait(false);
        return new JsonResult(state, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnPostSaveDraftAsync(
        int docKey,
        string? scanCountsJson,
        CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false }, ApprovalJson.CamelCase);

        var order = await _delivery.GetApprovedWithProgressAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new JsonResult(new { ok = false, error = "SO not found." }, ApprovalJson.CamelCase);

        var counts = TryParseScanCounts(scanCountsJson);
        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.SaveDraftAsync(
                docKey, order.SoNumber, counts, actor, ScanDocKinds.SalesOrder, cancellationToken)
            .ConfigureAwait(false);
        return new JsonResult(new { ok = true }, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnPostSubmitAsync(
        int docKey,
        string? scanCountsJson,
        CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return RedirectToPage("/ScanSO");

        var order = await _delivery.GetApprovedWithProgressAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return RedirectToPage("/ScanSO");

        var counts = TryParseScanCounts(scanCountsJson);
        if (counts.Count == 0)
        {
            TempData["ScanSubmitError"] = "Enter deliver qty for at least one line before submitting.";
            return RedirectToPage("/ScanSODetail", new { docKey });
        }

        var soLines = await _delivery.GetLinesWithDeliveredAsync(docKey, cancellationToken).ConfigureAwait(false);
        var transferLines = TransferLinesFromScanCounts(counts, soLines);
        if (transferLines.Count == 0)
        {
            TempData["ScanSubmitError"] = "Deliver quantities could not be matched to this SO for Delivery Order.";
            return RedirectToPage("/ScanSODetail", new { docKey });
        }

        transferLines = CapToOutstanding(soLines, transferLines);
        if (transferLines.Count == 0)
        {
            TempData["ScanSubmitError"] = "No outstanding quantity left to deliver on the selected lines.";
            return RedirectToPage("/ScanSODetail", new { docKey });
        }

        try
        {
            var result = await _doTransfer.TransferSoAsync(docKey, order.SoNumber, transferLines, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                TempData["ScanSubmitError"] = string.IsNullOrWhiteSpace(result.Error)
                    ? "Delivery Order transfer failed. Please try again."
                    : $"Delivery Order transfer failed: {result.Error}";
                return RedirectToPage("/ScanSODetail", new { docKey });
            }

            var actor = ScanPoAuditHelper.FromUser(User);
            await _scanSubmits.MarkSubmittedAsync(
                    docKey, order.SoNumber, counts, actor, ScanDocKinds.SalesOrder, cancellationToken)
                .ConfigureAwait(false);

            TempData["ScanSoSubmitMessage"] = $"Delivery Order {result.GrDocNo} created from {order.SoNumber}.";
            return RedirectToPage("/DeliveryOrders");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SO->DO transfer failed for {So}.", order.SoNumber);
            TempData["ScanSubmitError"] = ex.Message;
            return RedirectToPage("/ScanSODetail", new { docKey });
        }
    }

    public async Task<IActionResult> OnPostReopenAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return RedirectToPage("/ScanSO");

        var order = await _delivery.GetApprovedWithProgressAsync(docKey, cancellationToken).ConfigureAwait(false);
        var soNumber = order?.SoNumber ?? "";
        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.ClearSubmissionAsync(
                docKey, soNumber, actor, ScanDocKinds.SalesOrder, cancellationToken)
            .ConfigureAwait(false);

        var by = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Email : actor.DisplayName;
        TempData["ScanSoSubmitMessage"] = string.IsNullOrWhiteSpace(by)
            ? "SO reopened for scanning."
            : $"SO reopened for scanning by {by}.";
        return RedirectToPage("/ScanSODetail", new { docKey });
    }

    public async Task<IActionResult> OnPostResetScanAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false, error = "Invalid SO." }, ApprovalJson.CamelCase);

        var order = await _delivery.GetApprovedWithProgressAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new JsonResult(new { ok = false, error = "SO not found." }, ApprovalJson.CamelCase);

        var state = await _scanSubmits.GetStateAsync(docKey, ScanDocKinds.SalesOrder, cancellationToken)
            .ConfigureAwait(false);
        if (state.IsSubmitted)
            return new JsonResult(new { ok = false, error = "SO is submitted. Reopen to scan again." }, ApprovalJson.CamelCase);

        var actor = ScanPoAuditHelper.FromUser(User);
        await _scanSubmits.ClearDraftAsync(
                docKey, order.SoNumber, actor, ScanDocKinds.SalesOrder, cancellationToken)
            .ConfigureAwait(false);
        return new JsonResult(new { ok = true }, ApprovalJson.CamelCase);
    }

    private static IReadOnlyList<PurchaseRequestLineRow> ToMatchRows(IReadOnlyList<SalesOrderDeliveryLineRow> soLines) =>
        soLines
            .Where(l => l.OutstandingQty > 0)
            .Select(l => new PurchaseRequestLineRow
            {
                LineNo = l.LineNo,
                ItemCode = l.ItemCode,
                Description = l.Description,
                Project = l.Project,
                Qty = l.OutstandingQty,
                Sqty = l.OutstandingQty,
                Transferable = l.Transferable ?? true
            })
            .ToList();

    private static IReadOnlyList<GrTransferLineRequest> TransferLinesFromScanCounts(
        IReadOnlyDictionary<string, int> scanCounts,
        IReadOnlyList<SalesOrderDeliveryLineRow> soLines)
    {
        if (scanCounts.Count == 0 || soLines.Count == 0)
            return Array.Empty<GrTransferLineRequest>();

        var list = new List<GrTransferLineRequest>();
        foreach (var line in soLines)
        {
            var key = ScanPoValidationHelper.LineScanKey(line.LineNo);
            if (!scanCounts.TryGetValue(key, out var n) || n <= 0)
                continue;
            var code = (line.ItemCode ?? "").Trim();
            if (code.Length == 0)
                continue;
            var qty = Math.Min((decimal)n, line.OutstandingQty);
            if (qty <= 0)
                continue;
            list.Add(new GrTransferLineRequest(code, qty, line.LineNo));
        }

        return list;
    }

    private static IReadOnlyList<GrTransferLineRequest> CapToOutstanding(
        IReadOnlyList<SalesOrderDeliveryLineRow> soLines,
        IReadOnlyList<GrTransferLineRequest> lines)
    {
        var byLine = soLines.ToDictionary(l => l.LineNo);
        var result = new List<GrTransferLineRequest>();
        foreach (var req in lines)
        {
            if (req.Quantity <= 0) continue;
            if (req.PoLineNo is int lineNo && lineNo > 0 && byLine.TryGetValue(lineNo, out var line))
            {
                var qty = Math.Min(req.Quantity, line.OutstandingQty);
                if (qty <= 0) continue;
                result.Add(new GrTransferLineRequest(line.ItemCode, qty, line.LineNo));
            }
        }

        return result;
    }

    private static Dictionary<string, int> TryParseScanCounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, ApprovalJson.CamelCase);
            if (raw is null || raw.Count == 0)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, el) in raw)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                var n = el.ValueKind switch
                {
                    JsonValueKind.Number when el.TryGetInt32(out var i) => i,
                    JsonValueKind.Number => (int)Math.Ceiling(el.GetDecimal()),
                    JsonValueKind.String when int.TryParse(el.GetString(), out var p) => p,
                    _ => 0
                };
                if (n > 0)
                    result[key.Trim()] = n;
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
