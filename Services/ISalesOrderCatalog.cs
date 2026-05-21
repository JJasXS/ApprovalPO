using ApprovalPO.Models;

namespace ApprovalPO.Services;

public interface ISalesOrderCatalog
{
    Task<IReadOnlyList<SalesOrderRow>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SalesOrderLineRow>> GetSalesOrderLinesAsync(int docKey, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>SL_SO.UDF_SOSTATUS</c>: <c>Pending</c> / <c>Approved</c> / <c>Cancelled</c> / <c>Rejected</c>.</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetHeaderListStatusAsync(int docKey, string listStatus, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>SL_SODTL.TRANSFERABLE</c> for one line. Only when header <c>SL_SO.UDF_SOSTATUS</c> is pending (or null/blank).</summary>
    Task<(bool Success, string? ErrorMessage)> TrySetLineTransferableAsync(int docKey, int lineNo, bool transferable, CancellationToken cancellationToken = default);
}
