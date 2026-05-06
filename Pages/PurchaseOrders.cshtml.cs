using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Data;
using ApprovalPO.Models;
using ApprovalPO.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Pages;

[Authorize]
public class PurchaseOrdersModel : PageModel
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IOptions<ApprovalOptions> _approval;

    public PurchaseOrdersModel(IOptions<ApprovalOptions> approval) => _approval = approval;

    public IReadOnlyList<PurchaseOrderRow> Orders { get; private set; } = Array.Empty<PurchaseOrderRow>();

    public IReadOnlyList<string> DistinctVendors { get; private set; } = Array.Empty<string>();

    public decimal HighValueThreshold => _approval.Value.HighValueAmountThreshold;

    public void OnGet()
    {
        Orders = MockOrderCatalog.GetOrders();
        DistinctVendors = Orders.Select(o => o.Vendor).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToList();
    }

    public IActionResult OnGetOrdersJson()
    {
        var list = MockOrderCatalog.GetOrders();
        return new JsonResult(list, JsonCamel);
    }
}
