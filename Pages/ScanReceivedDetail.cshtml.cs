using ApprovalPO.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class ScanReceivedDetailModel : PageModel
{
    private readonly IGoodsReceiptCatalog _receipts;

    public ScanReceivedDetailModel(IGoodsReceiptCatalog receipts)
    {
        _receipts = receipts;
    }

    [BindProperty(SupportsGet = true)]
    public int DocKey { get; set; }

    public Models.GoodsReceiptRow? Receipt { get; private set; }

    public IReadOnlyList<Models.GoodsReceiptLineRow> Lines { get; private set; } =
        Array.Empty<Models.GoodsReceiptLineRow>();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (DocKey <= 0)
            return RedirectToPage("/ScanPO", new { tab = "received" });

        Receipt = await _receipts.GetReceiptAsync(DocKey, cancellationToken).ConfigureAwait(false);
        if (Receipt is null)
            return Page();

        Lines = await _receipts.GetReceiptLinesAsync(DocKey, cancellationToken).ConfigureAwait(false);
        return Page();
    }
}
