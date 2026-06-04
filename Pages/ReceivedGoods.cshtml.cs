using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Services.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class ReceivedGoodsModel : PageModel
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IGoodsReceiptCatalog _receipts;

    public ReceivedGoodsModel(IGoodsReceiptCatalog receipts)
    {
        _receipts = receipts;
    }

    public async Task<IActionResult> OnGetListJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _receipts.GetReceiptsAsync(cancellationToken).ConfigureAwait(false);
            return new JsonResult(list, JsonCamel);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }, JsonCamel) { StatusCode = 500 };
        }
    }
}
