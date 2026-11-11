using ApprovalPO.Models;

namespace ApprovalPO.Services.Orders;

public interface ISalesOrderCatalog
{
    Task<IReadOnlyList<SalesOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SalesOrderLineRow>> GetSalesOrderLinesAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Header <c>SL_SO.DOCAMT</c> for the given <paramref name="docKey"/>; null when the row is missing.</summary>
    Task<decimal?> GetHeaderAmountAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>SL_SO.UDF_SOSTATUS</c> with multi-level approval slots (same workflow as PO).</summary>
    Task<(bool Success, string? ErrorMessage, HeaderStatusChangeResult? Result)> TrySetHeaderListStatusAsync(
        int docKey,
        string listStatus,
        string? approverEmail = null,
        int approverUserLevel = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Reads partial approval slots for an SO header.</summary>
    Task<PoApprovalSlots?> GetApprovalSlotsAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>SL_SODTL.TRANSFERABLE</c> for one line. Only when header is pending.</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetLineTransferableAsync(int docKey, int lineNo, bool transferable, CancellationToken cancellationToken = default);
}
