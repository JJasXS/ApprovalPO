using ApprovalPO.Helpers;
using ApprovalPO.Services.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class DeliveryOrdersModel : PageModel
{
    private readonly IDeliveryOrderCatalog _orders;

    public DeliveryOrdersModel(IDeliveryOrderCatalog orders)
    {
        _orders = orders;
    }

    public async Task<IActionResult> OnGetListJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
            return new JsonResult(list, ApprovalJson.CamelCase);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }, ApprovalJson.CamelCase) { StatusCode = 500 };
        }
    }
}
