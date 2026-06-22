using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services;
using ApprovalPO.Services.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[ValidateAntiForgeryToken]
public class ScanPOModel : PageModel
{
    private readonly IPurchaseOrderScanQuery _scanQuery;
    private readonly IScanQrLinkResolver _scanResolver;
    private readonly IScanPoSubmitStore _scanSubmits;

    public ScanPOModel(
        IPurchaseOrderScanQuery scanQuery,
        IScanQrLinkResolver scanResolver,
        IScanPoSubmitStore scanSubmits)
    {
        _scanQuery = scanQuery;
        _scanResolver = scanResolver;
        _scanSubmits = scanSubmits;
    }

    public IReadOnlyList<PurchaseOrderRow> Orders { get; private set; } = Array.Empty<PurchaseOrderRow>();

    public async Task<IActionResult> OnGetAsync(string? tab, CancellationToken cancellationToken)
    {
        if (string.Equals(tab, "received", StringComparison.OrdinalIgnoreCase))
            return RedirectToPage("/ReceivedGoods");

        Orders = await _scanQuery.ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnGetOrdersJsonAsync(CancellationToken cancellationToken)
    {
        var list = await LoadScanListItemsAsync(cancellationToken).ConfigureAwait(false);
        return new JsonResult(list, ApprovalJson.CamelCase);
    }

    public async Task<IActionResult> OnGetResolveScanAsync(string url, [FromQuery] string[]? codes, CancellationToken cancellationToken)
    {
        var result = await _scanResolver.ResolveAsync(url ?? "", codes, cancellationToken).ConfigureAwait(false);
        return new JsonResult(result, ApprovalJson.CamelCase);
    }

    private async Task<IReadOnlyList<ScanPoOrderListItem>> LoadScanListItemsAsync(CancellationToken cancellationToken)
    {
        var orders = await _scanQuery.ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        var submitSummaries = await _scanSubmits.GetSubmitSummariesAsync(cancellationToken).ConfigureAwait(false);
        var submitByDoc = submitSummaries.ToDictionary(s => s.DocKey);

        return orders
            .Select(o =>
            {
                submitByDoc.TryGetValue(o.DocKey, out var sub);
                return new ScanPoOrderListItem
                {
                    DocKey = o.DocKey,
                    PoNumber = o.PoNumber,
                    OrderDate = o.OrderDate,
                    Amount = o.Amount,
                    ScanSubmitted = sub is not null,
                    SubmittedAtUtc = sub?.SubmittedAtUtc,
                    SubmittedByName = string.IsNullOrWhiteSpace(sub?.SubmittedByName)
                        ? sub?.SubmittedByEmail
                        : sub?.SubmittedByName
                };
            })
            .ToList();
    }
}
