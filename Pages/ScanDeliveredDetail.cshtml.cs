using ApprovalPO.Services.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class ScanDeliveredDetailModel : PageModel
{
    private readonly IDeliveryOrderCatalog _orders;

    public ScanDeliveredDetailModel(IDeliveryOrderCatalog orders)
    {
        _orders = orders;
    }

    [BindProperty(SupportsGet = true)]
    public int DocKey { get; set; }

    public Models.DeliveryOrderRow? Order { get; private set; }

    public IReadOnlyList<Models.DeliveryOrderLineRow> Lines { get; private set; } =
        Array.Empty<Models.DeliveryOrderLineRow>();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (DocKey <= 0)
            return RedirectToPage("/DeliveryOrders");

        Order = await _orders.GetOrderAsync(DocKey, cancellationToken).ConfigureAwait(false);
        if (Order is null)
            return Page();

        Lines = await _orders.GetOrderLinesAsync(DocKey, cancellationToken).ConfigureAwait(false);
        return Page();
    }
}
