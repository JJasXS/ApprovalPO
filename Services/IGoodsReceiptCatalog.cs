using ApprovalPO.Models;

namespace ApprovalPO.Services;

public interface IGoodsReceiptCatalog
{
    Task<IReadOnlyList<GoodsReceiptListItem>> GetReceiptsAsync(CancellationToken cancellationToken = default);

    Task<GoodsReceiptRow?> GetReceiptAsync(int docKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoodsReceiptLineRow>> GetReceiptLinesAsync(int docKey, CancellationToken cancellationToken = default);
}
