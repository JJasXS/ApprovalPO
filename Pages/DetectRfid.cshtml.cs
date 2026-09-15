using ApprovalPO.Services.Stock;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class DetectRfidModel : PageModel
{
    private readonly IStockItemLookup _stock;

    public DetectRfidModel(IStockItemLookup stock)
    {
        _stock = stock;
    }

    /// <summary>Detect window in seconds (clamped 2–5).</summary>
    public int DetectSeconds { get; private set; } = 3;

    public void OnGet()
    {
        DetectSeconds = 3;
    }

    public async Task<IActionResult> OnGetExpectedJsonAsync(CancellationToken cancellationToken)
    {
        var items = await _stock.ListEpcStyleCodesAsync(cancellationToken).ConfigureAwait(false);
        return new JsonResult(new
        {
            count = items.Count,
            items = items.Select(i => new { code = i.Code, description = i.Description }).ToList()
        });
    }
}
