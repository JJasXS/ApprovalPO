using ApprovalPO.Models;

namespace ApprovalPO.Services;

public interface IPurchaseOrderCatalog
{
    Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseRequestLineRow>> GetPurchaseRequestLinesAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PQ.TRANSFERABLE</c>: null = pending, true = approved, false = rejected/cancelled.</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetTransferableAsync(int docKey, bool? transferable, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PQDTL.TRANSFERABLE</c> for one line (matches <c>COALESCE(SEQ,0)</c> to LINENO). Only when header <c>PH_PQ.TRANSFERABLE</c> is still null (pending).</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetLineTransferableAsync(int docKey, int lineNo, bool transferable, CancellationToken cancellationToken = default);
}
