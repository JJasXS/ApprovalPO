using ApprovalPO.Models;

namespace ApprovalPO.Services;

public interface IPurchaseOrderCatalog
{
    Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseRequestLineRow>> GetPurchaseRequestLinesAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PO.UDF_POSTATUS</c>: <c>PENDING</c> / <c>APPROVED</c> / <c>CANCELLED</c> (from tri-state <c>transferable</c>: null / true / false).</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetTransferableAsync(int docKey, bool? transferable, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PODTL.TRANSFERABLE</c> for one line (matches <c>COALESCE(SEQ,0)</c> to LINENO). Only when header <c>PH_PO.UDF_POSTATUS</c> is pending (or null/blank).</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetLineTransferableAsync(int docKey, int lineNo, bool transferable, CancellationToken cancellationToken = default);
}
