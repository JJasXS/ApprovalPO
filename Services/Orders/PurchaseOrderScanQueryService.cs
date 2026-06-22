using ApprovalPO.Models;
using ApprovalPO.Helpers;

namespace ApprovalPO.Services.Orders;

/// <summary>Approved PO queries for the Scan PO module (encapsulates repeated list/filter logic).</summary>
public interface IPurchaseOrderScanQuery
{
    Task<IReadOnlyList<PurchaseOrderRow>> ListApprovedAsync(CancellationToken cancellationToken = default);

    Task<PurchaseOrderRow?> GetApprovedByDocKeyAsync(int docKey, CancellationToken cancellationToken = default);

    Task<PurchaseOrderRow?> FindApprovedByPoNumberAsync(string poNumber, CancellationToken cancellationToken = default);
}

public sealed class PurchaseOrderScanQueryService : IPurchaseOrderScanQuery
{
    private readonly IPurchaseOrderCatalog _orders;

    public PurchaseOrderScanQueryService(IPurchaseOrderCatalog orders)
    {
        _orders = orders;
    }

    public async Task<IReadOnlyList<PurchaseOrderRow>> ListApprovedAsync(CancellationToken cancellationToken = default)
    {
        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(o => string.Equals(o.Status, "Approved", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.PoNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PurchaseOrderRow?> GetApprovedByDocKeyAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0) return null;
        var list = await ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(o => o.DocKey == docKey);
    }

    public async Task<PurchaseOrderRow?> FindApprovedByPoNumberAsync(string poNumber, CancellationToken cancellationToken = default)
    {
        var needle = ScanPoValidationHelper.NormalizeDocumentNumber(poNumber);
        if (needle.Length == 0)
            return null;

        var list = await ListApprovedAsync(cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(o =>
            ScanPoValidationHelper.DocumentNumbersMatch(o.PoNumber, needle));
    }
}
