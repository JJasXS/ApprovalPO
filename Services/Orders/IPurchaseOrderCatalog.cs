using ApprovalPO.Models;

namespace ApprovalPO.Services.Orders;

public interface IPurchaseOrderCatalog
{
    Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseRequestLineRow>> GetPurchaseRequestLinesAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Header <c>PH_PO.DOCAMT</c> for the given <paramref name="docKey"/>; null when the row is missing.</summary>
    Task<decimal?> GetHeaderAmountAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PO.UDF_POSTATUS</c> from list tab: <c>Pending</c> / <c>Approved</c> / <c>Cancelled</c> / <c>Rejected</c>.</summary>
    /// <param name="approverUserLevel">
    /// For Approved status: the approver's level (1-3). A level-N approver fills all empty slots 1..N,
    /// so higher levels cover lower ones automatically.
    /// </param>
    Task<(bool Success, string? ErrorMessage, HeaderStatusChangeResult? Result)> TrySetHeaderListStatusAsync(
        int docKey,
        string listStatus,
        string? approverEmail = null,
        int approverUserLevel = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Reads partial approval slots for a PO header.</summary>
    Task<PoApprovalSlots?> GetApprovalSlotsAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>PH_PODTL.TRANSFERABLE</c> for one line (matches <c>COALESCE(SEQ,0)</c> to LINENO). Only when header <c>PH_PO.UDF_POSTATUS</c> is pending (or null/blank).</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetLineTransferableAsync(int docKey, int lineNo, bool transferable, CancellationToken cancellationToken = default);
}
