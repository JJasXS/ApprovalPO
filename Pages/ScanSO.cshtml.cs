using ApprovalPO.Helpers;
using ApprovalPO.Services.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[ValidateAntiForgeryToken]
public class ScanSOModel : PageModel
{
    private readonly ISalesOrderDeliveryQuery _delivery;

    public ScanSOModel(ISalesOrderDeliveryQuery delivery)
    {
        _delivery = delivery;
    }

    public async Task<IActionResult> OnGetAsync(string? tab, CancellationToken cancellationToken)
    {
        if (string.Equals(tab, "delivered", StringComparison.OrdinalIgnoreCase))
            return RedirectToPage("/DeliveryOrders");

        return Page();
    }

    public async Task<IActionResult> OnGetOrdersJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _delivery.ListApprovedWithProgressAsync(cancellationToken).ConfigureAwait(false);
            return new JsonResult(list, ApprovalJson.CamelCase);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }, ApprovalJson.CamelCase) { StatusCode = 500 };
        }
    }
}
