using ApprovalPO.Models;
using ApprovalPO.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class ScanPODetailModel : PageModel
{
    private readonly IPurchaseOrderCatalog _orders;
    private readonly IScanQrLinkResolver _scanResolver;

    public ScanPODetailModel(IPurchaseOrderCatalog orders, IScanQrLinkResolver scanResolver)
    {
        _orders = orders;
        _scanResolver = scanResolver;
    }

    [BindProperty(SupportsGet = true)]
    public int DocKey { get; set; }

    public PurchaseOrderRow? Order { get; private set; }

    public IReadOnlyList<PurchaseRequestLineRow> Lines { get; private set; } = Array.Empty<PurchaseRequestLineRow>();

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

        Lines = await _orders.GetPurchaseRequestLinesAsync(DocKey, cancellationToken).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnGetResolveScanAsync(string url, CancellationToken cancellationToken)
    {
        var result = await _scanResolver.ResolveAsync(url ?? "", cancellationToken).ConfigureAwait(false);
        return new JsonResult(result);
    }
}
