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
    private readonly IScanPoSubmitStore _scanSubmits;
    private readonly ILogger<ScanSODetailModel> _logger;

    public ScanSODetailModel(
        ISalesOrderDeliveryQuery delivery,
        IDeliveryOrderTransfer doTransfer,
        IScanPoSubmitStore scanSubmits,
        ILogger<ScanSODetailModel> logger)
    {
        _delivery = delivery;
        _doTransfer = doTransfer;
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

    public async Task<IActionResult> OnPostConfirmDeliveryAsync(
        int docKey,
        string? transferLinesJson,
        CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new JsonResult(new { ok = false, error = "Invalid document." }, ApprovalJson.CamelCase);

        var order = await _delivery.GetApprovedWithProgressAsync(docKey, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new JsonResult(new { ok = false, error = "Sales order not found or not approved." }, ApprovalJson.CamelCase);

        var transferLines = TryParseTransferLines(transferLinesJson);
        if (transferLines.Count == 0)
        {
            return new JsonResult(new
            {
                ok = false,
                error = "Enter deliver quantities on one or more lines with outstanding stock."
            }, ApprovalJson.CamelCase);
        }

        transferLines = await CapToOutstandingAsync(docKey, transferLines, cancellationToken).ConfigureAwait(false);
        if (transferLines.Count == 0)
        {
            return new JsonResult(new
            {
                ok = false,
                error = "No outstanding quantity left to deliver on the selected lines."
            }, ApprovalJson.CamelCase);
        }

        try
        {
            var result = await _doTransfer.TransferSoAsync(docKey, order.SoNumber, transferLines, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                return new JsonResult(new
                {
                    ok = false,
                    error = result.Error ?? "Delivery Order transfer failed."
                }, ApprovalJson.CamelCase);
            }

            var actor = ScanPoAuditHelper.FromUser(User);
            var counts = transferLines.ToDictionary(
                l => l.ItemCode,
                l => (int)Math.Ceiling(l.Quantity),
                StringComparer.OrdinalIgnoreCase);
            await _scanSubmits.MarkSubmittedAsync(
                    docKey, order.SoNumber, counts, actor, ScanDocKinds.SalesOrder, cancellationToken)
                .ConfigureAwait(false);

            TempData["ScanSoSubmitMessage"] = $"Delivery Order {result.GrDocNo} created from {order.SoNumber}.";
            return new JsonResult(new
            {
                ok = true,
                doNumber = result.GrDocNo,
                redirectUrl = Url.Page("/DeliveryOrders")
            }, ApprovalJson.CamelCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SO->DO transfer failed for {So}.", order.SoNumber);
            return new JsonResult(new { ok = false, error = ex.Message }, ApprovalJson.CamelCase);
        }
    }

    private async Task<IReadOnlyList<GrTransferLineRequest>> CapToOutstandingAsync(
        int docKey,
        IReadOnlyList<GrTransferLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var soLines = await _delivery.GetLinesWithDeliveredAsync(docKey, cancellationToken).ConfigureAwait(false);
        var byLine = soLines.ToDictionary(l => l.LineNo);
        var byCode = soLines
            .GroupBy(l => l.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<GrTransferLineRequest>();
        foreach (var req in lines)
        {
            if (req.Quantity <= 0) continue;

            if (req.PoLineNo is int lineNo && lineNo > 0 && byLine.TryGetValue(lineNo, out var line))
            {
                var qty = Math.Min(req.Quantity, line.OutstandingQty);
                if (qty <= 0) continue;
                result.Add(new GrTransferLineRequest(line.ItemCode, qty, line.LineNo));
                continue;
            }

            var code = (req.ItemCode ?? "").Trim();
            if (code.Length == 0 || !byCode.TryGetValue(code, out var matches)) continue;
            var remaining = req.Quantity;
            foreach (var match in matches)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, match.OutstandingQty);
                if (take <= 0) continue;
                result.Add(new GrTransferLineRequest(match.ItemCode, take, match.LineNo));
                remaining -= take;
            }
        }

        return result;
    }

    private static IReadOnlyList<GrTransferLineRequest> TryParseTransferLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<GrTransferLineRequest>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<TransferLineDto>>(json, ApprovalJson.CamelCase);
            if (rows is null || rows.Count == 0)
                return Array.Empty<GrTransferLineRequest>();

            var list = new List<GrTransferLineRequest>();
            foreach (var row in rows)
            {
                var code = (row.ItemCode ?? "").Trim();
                if (code.Length == 0 || row.Quantity <= 0) continue;
                list.Add(new GrTransferLineRequest(code, row.Quantity, row.LineNo > 0 ? row.LineNo : null));
            }
            return list;
        }
        catch
        {
            return Array.Empty<GrTransferLineRequest>();
        }
    }

    private sealed class TransferLineDto
    {
        public string? ItemCode { get; set; }
        public decimal Quantity { get; set; }
        public int LineNo { get; set; }
    }
}
