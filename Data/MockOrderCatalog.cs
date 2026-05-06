using ApprovalPO.Models;

namespace ApprovalPO.Data;

public static class MockOrderCatalog
{
    public static IReadOnlyList<PurchaseOrderRow> GetOrders() =>
        new List<PurchaseOrderRow>
        {
            new()
            {
                PoNumber = "PO-24007",
                Vendor = "Contoso Ltd",
                Amount = 12500.00m,
                Status = "Pending",
                Description = "High-value equipment order.",
                OrderDate = new DateTime(2026, 5, 5)
            },
            new()
            {
                PoNumber = "PO-24006",
                Vendor = "Tailspin Toys",
                Amount = 1890.00m,
                Status = "Pending",
                Description = "Rush order — mobile kiosk supplies.",
                OrderDate = new DateTime(2026, 5, 4)
            },
            new()
            {
                PoNumber = "PO-24005",
                Vendor = "Wide World Importers",
                Amount = 675.40m,
                Status = "Pending",
                Description = "New vendor onboarding kit.",
                OrderDate = new DateTime(2026, 5, 3)
            },
            new()
            {
                PoNumber = "PO-24004",
                Vendor = "Fabrikam",
                Amount = 310.25m,
                Status = "Pending",
                Description = "Direct bill.",
                OrderDate = new DateTime(2026, 5, 2)
            },
            new()
            {
                PoNumber = "PO-24003",
                Vendor = "Contoso Ltd",
                Amount = 4420.00m,
                Status = "Pending",
                Description = "Locked vendor agreement.",
                OrderDate = new DateTime(2026, 4, 28)
            },
            new()
            {
                PoNumber = "PO-24002",
                Vendor = "Northwind Traders",
                Amount = 980.50m,
                Status = "Pending",
                Description = "Quarterly consumables.",
                OrderDate = new DateTime(2026, 4, 20)
            },
            new()
            {
                PoNumber = "PO-24001",
                Vendor = "Acme Supplies",
                Amount = 1250.00m,
                Status = "Pending",
                Description = "Office hardware replenishment.",
                OrderDate = new DateTime(2026, 4, 15)
            },
        };
}
