using ApprovalPO.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

[Authorize]
public class PurchaseOrdersModel : PageModel
{
    public IReadOnlyList<PurchaseOrderRow> Orders { get; private set; } = Array.Empty<PurchaseOrderRow>();

    public void OnGet()
    {
        Orders = MockPurchaseOrders();
    }

    private static IReadOnlyList<PurchaseOrderRow> MockPurchaseOrders()
    {
        // Newest PO numbers first.
        return new List<PurchaseOrderRow>
        {
            new()
            {
                PoNumber = "PO-24006",
                Vendor = "Tailspin Toys",
                Amount = 1890.00m,
                Status = "Pending",
                Description = "Rush order — mobile kiosk supplies."
            },
            new()
            {
                PoNumber = "PO-24005",
                Vendor = "Wide World Importers",
                Amount = 675.40m,
                Status = "Pending",
                Description = "New vendor onboarding kit."
            },
            new()
            {
                PoNumber = "PO-24004",
                Vendor = "Fabrikam",
                Amount = 310.25m,
                Status = "Pending",
                Description = "Direct bill."
            },
            new()
            {
                PoNumber = "PO-24003",
                Vendor = "Contoso Ltd",
                Amount = 4420.00m,
                Status = "Pending",
                Description = "Locked vendor agreement."
            },
            new()
            {
                PoNumber = "PO-24002",
                Vendor = "Northwind Traders",
                Amount = 980.50m,
                Status = "Pending",
                Description = "Quarterly consumables."
            },
            new()
            {
                PoNumber = "PO-24001",
                Vendor = "Acme Supplies",
                Amount = 1250.00m,
                Status = "Pending",
                Description = "Office hardware replenishment."
            },
        };
    }
}
