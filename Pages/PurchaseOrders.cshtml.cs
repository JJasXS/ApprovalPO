using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Models;
using ApprovalPO.Options;
using ApprovalPO.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Pages;

public sealed class SetTransferableRequest
{
    public int DocKey { get; set; }

    /// <summary>Pending, Approved, or Rejected (cancelled).</summary>
    public string ListStatus { get; set; } = "";
}

public sealed class SetLineTransferableRequest
{
    public int DocKey { get; set; }

    /// <summary>Matches <c>PH_PQDTL.SEQ</c> (same as list JSON <c>lineNo</c>, <c>COALESCE(SEQ,0)</c>).</summary>
    public int LineNo { get; set; }

    public bool Transferable { get; set; }
}

[ValidateAntiForgeryToken]
public class PurchaseOrdersModel : PageModel
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IOptions<ApprovalOptions> _approval;
    private readonly IPurchaseOrderCatalog _orders;

    public PurchaseOrdersModel(IOptions<ApprovalOptions> approval, IPurchaseOrderCatalog orders)
    {
        _approval = approval;
        _orders = orders;
    }

    public IReadOnlyList<PurchaseOrderRow> Orders { get; private set; } = Array.Empty<PurchaseOrderRow>();

    public IReadOnlyList<string> DistinctVendors { get; private set; } = Array.Empty<string>();

    public decimal HighValueThreshold => _approval.Value.HighValueAmountThreshold;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Orders = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        DistinctVendors = Orders.Select(o => o.Vendor).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToList();
    }

    public async Task<IActionResult> OnGetOrdersJsonAsync(CancellationToken cancellationToken)
    {
        var list = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        return new JsonResult(list, JsonCamel);
    }

    public async Task<IActionResult> OnGetLinesJsonAsync(int docKey, CancellationToken cancellationToken)
    {
        if (docKey <= 0)
            return new BadRequestObjectResult(new { error = "docKey must be a positive integer." });

        var lines = await _orders.GetPurchaseRequestLinesAsync(docKey, cancellationToken).ConfigureAwait(false);
        return new JsonResult(lines, JsonCamel);
    }

    public async Task<IActionResult> OnPostSetTransferableAsync(
        [FromBody] SetTransferableRequest? body,
        CancellationToken cancellationToken)
    {
        if (body == null || body.DocKey <= 0)
            return new JsonResult(new { ok = false, error = "Invalid request." }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };

        if (!TryMapListStatus(body.ListStatus, out var transferable))
            return new JsonResult(new { ok = false, error = "ListStatus must be Pending, Approved, or Rejected." }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };

        var (ok, err) = await _orders.TrySetTransferableAsync(body.DocKey, transferable, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return new JsonResult(new { ok = false, error = err ?? "Update failed." }, JsonCamel) { StatusCode = StatusCodes.Status409Conflict };

        return new JsonResult(new { ok = true }, JsonCamel);
    }

    public async Task<IActionResult> OnPostSetLineTransferableAsync(
        [FromBody] SetLineTransferableRequest? body,
        CancellationToken cancellationToken)
    {
        if (body == null || body.DocKey <= 0 || body.LineNo < 0)
            return new JsonResult(new { ok = false, error = "Invalid request." }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };

        var (ok, err) = await _orders.TrySetLineTransferableAsync(body.DocKey, body.LineNo, body.Transferable, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return new JsonResult(new { ok = false, error = err ?? "Update failed." }, JsonCamel) { StatusCode = StatusCodes.Status409Conflict };

        return new JsonResult(new { ok = true }, JsonCamel);
    }

    private static bool TryMapListStatus(string? raw, out bool? transferable)
    {
        transferable = null;
        var s = (raw ?? "").Trim();
        if (s.Equals("Approved", StringComparison.OrdinalIgnoreCase))
        {
            transferable = true;
            return true;
        }

        if (s.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
        {
            transferable = false;
            return true;
        }

        if (s.Equals("Pending", StringComparison.OrdinalIgnoreCase))
        {
            transferable = null;
            return true;
        }

        return false;
    }
}
