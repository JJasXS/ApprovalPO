using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Models;
using ApprovalPO.Options;
using ApprovalPO.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Pages;

public sealed class SoSetTransferableRequest
{
    public int DocKey { get; set; }

    /// <summary>Pending, Approved, Cancelled, or Rejected (maps to <c>UDF_SOSTATUS</c>).</summary>
    public string ListStatus { get; set; } = "";
}

public sealed class SoSetLineTransferableRequest
{
    public int DocKey { get; set; }
    public int LineNo { get; set; }
    public bool Transferable { get; set; }
}

[ValidateAntiForgeryToken]
public class SalesOrdersModel : PageModel
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IOptions<ApprovalOptions> _approval;
    private readonly ISalesOrderCatalog _orders;

    public SalesOrdersModel(
        IOptions<ApprovalOptions> approval,
        ISalesOrderCatalog orders)
    {
        _approval = approval;
        _orders = orders;
    }

    public IReadOnlyList<SalesOrderRow> Orders { get; private set; } = Array.Empty<SalesOrderRow>();

    public decimal HighValueThreshold => _approval.Value.HighValueAmountThreshold;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Orders = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
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

        var lines = await _orders.GetSalesOrderLinesAsync(docKey, cancellationToken).ConfigureAwait(false);
        return new JsonResult(lines, JsonCamel);
    }

    public async Task<IActionResult> OnGetPendingNotifySnapshotAsync(CancellationToken cancellationToken)
    {
        var list = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var pending = list
            .Where(o => string.Equals(o.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.DocKey)
            .Select(o => new PendingItem(o.DocKey, o.SoNumber))
            .ToList();

        return new JsonResult(new PendingSnapshot(pending.Count, pending), JsonCamel);
    }

    private sealed record PendingItem(int DocKey, string PoNumber);
    private sealed record PendingSnapshot(int PendingCount, IReadOnlyList<PendingItem> Pending);

    public async Task<IActionResult> OnPostSetTransferableAsync(
        [FromBody] SoSetTransferableRequest? body,
        CancellationToken cancellationToken)
    {
        if (body == null || body.DocKey <= 0)
            return new JsonResult(new { ok = false, error = "Invalid request." }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };

        if (!TryNormalizeListStatus(body.ListStatus, out var normalized))
            return new JsonResult(new { ok = false, error = "ListStatus must be Pending, Approved, Cancelled, or Rejected." }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };

        var (ok, err) = await _orders.TrySetHeaderListStatusAsync(body.DocKey, normalized, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return new JsonResult(new { ok = false, error = err ?? "Update failed." }, JsonCamel) { StatusCode = StatusCodes.Status409Conflict };

        return new JsonResult(new { ok = true }, JsonCamel);
    }

    public async Task<IActionResult> OnPostSetLineTransferableAsync(
        [FromBody] SoSetLineTransferableRequest? body,
        CancellationToken cancellationToken)
    {
        if (body == null || body.DocKey <= 0 || body.LineNo < 0)
            return new JsonResult(new { ok = false, error = "Invalid request." }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };

        var (ok, err) = await _orders.TrySetLineTransferableAsync(body.DocKey, body.LineNo, body.Transferable, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return new JsonResult(new { ok = false, error = err ?? "Update failed." }, JsonCamel) { StatusCode = StatusCodes.Status409Conflict };

        return new JsonResult(new { ok = true }, JsonCamel);
    }

    private static bool TryNormalizeListStatus(string? raw, out string normalized)
    {
        normalized = "";
        var s = (raw ?? "").Trim();
        if (s.Equals("Pending",   StringComparison.OrdinalIgnoreCase)) { normalized = "Pending";   return true; }
        if (s.Equals("Approved",  StringComparison.OrdinalIgnoreCase)) { normalized = "Approved";  return true; }
        if (s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("Canceled",  StringComparison.OrdinalIgnoreCase)) { normalized = "Cancelled"; return true; }
        if (s.Equals("Rejected",  StringComparison.OrdinalIgnoreCase)) { normalized = "Rejected";  return true; }
        return false;
    }
}
