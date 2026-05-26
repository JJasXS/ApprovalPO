using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services;
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

    private readonly IPurchaseOrderCatalog _orders;
    private readonly IScanQrLinkResolver _scanResolver;
    private readonly IScanPoSubmitStore _scanSubmits;

    public ScanPODetailModel(IPurchaseOrderCatalog orders, IScanQrLinkResolver scanResolver, IScanPoSubmitStore scanSubmits)
    {
        _orders = orders;
        _scanResolver = scanResolver;
        _scanSubmits = scanSubmits;
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
