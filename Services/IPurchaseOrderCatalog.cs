using ApprovalPO.Models;

namespace ApprovalPO.Services;

public interface IPurchaseOrderCatalog
{
    Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseRequestLineRow>> GetPurchaseRequestLinesAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PO.UDF_POSTATUS</c> from list tab: <c>Pending</c> / <c>Approved</c> / <c>Cancelled</c> / <c>Rejected</c>.</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetHeaderListStatusAsync(int docKey, string listStatus, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PODTL.TRANSFERABLE</c> for one line (matches <c>COALESCE(SEQ,0)</c> to LINENO). Only when header <c>PH_PO.UDF_POSTATUS</c> is pending (or null/blank).</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetLineTransferableAsync(int docKey, int lineNo, bool transferable, CancellationToken cancellationToken = default);
}
