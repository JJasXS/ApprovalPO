using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Models;
using ApprovalPO.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class ScanPOModel : PageModel
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IPurchaseOrderCatalog _orders;
    private readonly IScanQrLinkResolver _scanResolver;
    private readonly IScanPoSubmitStore _scanSubmits;
    private readonly IGoodsReceiptCatalog _receipts;

    public ScanPOModel(
        IPurchaseOrderCatalog orders,
        IScanQrLinkResolver scanResolver,
        IScanPoSubmitStore scanSubmits,
        IGoodsReceiptCatalog receipts)
    {
        _orders = orders;
        _scanResolver = scanResolver;
        _scanSubmits = scanSubmits;
        _receipts = receipts;
    }

    public IReadOnlyList<PurchaseOrderRow> Orders { get; private set; } = Array.Empty<PurchaseOrderRow>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Orders = await LoadOrdersForScanAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnGetOrdersJsonAsync(CancellationToken cancellationToken)
    {
        var list = await LoadScanListItemsAsync(cancellationToken).ConfigureAwait(false);
        return new JsonResult(list, JsonCamel);
    }

    public async Task<IActionResult> OnGetResolveScanAsync(string url, [FromQuery] string[]? codes, CancellationToken cancellationToken)
    {
        var result = await _scanResolver.ResolveAsync(url ?? "", codes, cancellationToken).ConfigureAwait(false);
        return new JsonResult(result);
    }

    public async Task<IActionResult> OnGetReceivedGoodsJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _receipts.GetReceiptsAsync(cancellationToken).ConfigureAwait(false);
            return new JsonResult(list, JsonCamel);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }, JsonCamel) { StatusCode = 500 };
        }
    }

    private async Task<IReadOnlyList<PurchaseOrderRow>> LoadOrdersForScanAsync(CancellationToken cancellationToken)
    {
        var all = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        return all
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.PoNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<ScanPoOrderListItem>> LoadScanListItemsAsync(CancellationToken cancellationToken)
    {
        var orders = await LoadOrdersForScanAsync(cancellationToken).ConfigureAwait(false);
        var submitSummaries = await _scanSubmits.GetSubmitSummariesAsync(cancellationToken).ConfigureAwait(false);
        var submitByDoc = submitSummaries.ToDictionary(s => s.DocKey);

        return orders
            .Select(o =>
            {
                submitByDoc.TryGetValue(o.DocKey, out var sub);
                return new ScanPoOrderListItem
                {
                    DocKey = o.DocKey,
                    PoNumber = o.PoNumber,
                    OrderDate = o.OrderDate,
                    Amount = o.Amount,
                    ScanSubmitted = sub is not null,
                    SubmittedAtUtc = sub?.SubmittedAtUtc,
                    SubmittedByName = string.IsNullOrWhiteSpace(sub?.SubmittedByName)
                        ? sub?.SubmittedByEmail
                        : sub?.SubmittedByName
                };
            })
            .ToList();
    }
}
