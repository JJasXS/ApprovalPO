using ApprovalPO.Models;

namespace ApprovalPO.Services.Orders;

public interface IDeliveryOrderCatalog
{
    Task<IReadOnlyList<DeliveryOrderListItem>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<DeliveryOrderRow?> GetOrderAsync(int docKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliveryOrderLineRow>> GetOrderLinesAsync(int docKey, CancellationToken cancellationToken = default);
}
