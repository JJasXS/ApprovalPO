using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Models;
using ApprovalPO.Options;
using ApprovalPO.Services;
using ApprovalPO.Services.Auth;
using ApprovalPO.Services.Orders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Pages;

public sealed class SetTransferableRequest
{
    public int DocKey { get; set; }

    /// <summary>Pending, Approved, Cancelled, or Rejected (maps to <c>UDF_POSTATUS</c>).</summary>
    public string ListStatus { get; set; } = "";
}

public sealed class SetLineTransferableRequest
{
    public int DocKey { get; set; }

    /// <summary>Matches <c>PH_PODTL.SEQ</c> (same as list JSON <c>lineNo</c>, <c>COALESCE(SEQ,0)</c>).</summary>
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
    private readonly IOptions<WebPushOptions> _webPush;
    private readonly IPurchaseOrderCatalog _orders;
    private readonly IApprovalAuthority _authority;
    private readonly IWebHostEnvironment _env;

    public PurchaseOrdersModel(
        IOptions<ApprovalOptions> approval,
        IOptions<WebPushOptions> webPush,
        IPurchaseOrderCatalog orders,
        IApprovalAuthority authority,
        IWebHostEnvironment env)
    {
        _approval = approval;
        _webPush = webPush;
        _orders = orders;
        _authority = authority;
        _env = env;
    }

    /// <summary>Browser Web Push (VAPID) keys are present; client may register a subscription.</summary>
    public bool WebPushEnabled => _webPush.Value.HasVapidKeys;

    public string? WebPushPublicKey => WebPushEnabled ? _webPush.Value.PublicKey.Trim() : null;

    /// <summary>Development-only UI for quick notification smoke tests.</summary>
    public bool ShowDevNotifyTools => _env.IsDevelopment();

    public int PendingNotifyPollMilliseconds
    {
        get
        {
            var sec = Math.Clamp(_approval.Value.PendingNotifyPollSeconds, 30, 600);
            return sec * 1000;
        }
    }

    public IReadOnlyList<PurchaseOrderRow> Orders { get; private set; } = Array.Empty<PurchaseOrderRow>();

    public decimal HighValueThreshold => _approval.Value.HighValueAmountThreshold;

    public decimal? AppFrom => SyUserApprovalMapper.ReadDecimalClaim(User, ApprovalClaimTypes.AppFrom);

    public decimal? AppTo => SyUserApprovalMapper.ReadDecimalClaim(User, ApprovalClaimTypes.AppTo);

    public decimal? AppLevel => SyUserApprovalMapper.ReadDecimalClaim(User, ApprovalClaimTypes.AppLevel);

    public string? UserEmail => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value?.Trim();

    public bool CanApprove => SyUserApprovalMapper.CanApproveFromClaims(User);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Orders = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnGetOrdersJsonAsync(CancellationToken cancellationToken)
    {
        var list = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        return new JsonResult(list, JsonCamel);
    }

    /// <summary>Temporary debug: returns raw SY_USER UDF claims for the signed-in user.</summary>
    public IActionResult OnGetDebugClaims()
    {
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "";
        return new JsonResult(new
        {
            email,
            canApprove = CanApprove,
            cookieAppRole = User.FindFirst(ApprovalClaimTypes.AppRole)?.Value,
            cookieAppLevel = User.FindFirst(ApprovalClaimTypes.AppLevel)?.Value,
            cookieAppFrom = User.FindFirst(ApprovalClaimTypes.AppFrom)?.Value,
            cookieAppTo = User.FindFirst(ApprovalClaimTypes.AppTo)?.Value,
            allClaims = User.Claims.Select(c => new { c.Type, c.Value }).ToList()
        }, JsonCamel);
    }

    /// <summary>Lightweight snapshot for desktop notifications: orders in the Pending tab (e.g. <c>UDF_POSTATUS</c> <c>PENDING</c> when using default list SQL).</summary>
    public async Task<IActionResult> OnGetPendingNotifySnapshotAsync(CancellationToken cancellationToken)
    {
        var list = await _orders.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var pending = list
            .Where(o => string.Equals(o.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.DocKey)
            .Select(o => new PendingNotifyItem(o.DocKey, o.PoNumber))
            .ToList();

        return new JsonResult(new PendingNotifySnapshot(pending.Count, pending), JsonCamel);
    }

    private sealed record PendingNotifyItem(int DocKey, string PoNumber);

    private sealed record PendingNotifySnapshot(int PendingCount, IReadOnlyList<PendingNotifyItem> Pending);

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

        if (!TryNormalizeListStatus(body.ListStatus, out var normalized))
            return new JsonResult(new { ok = false, error = "ListStatus must be Pending, Approved, Cancelled, or Rejected." }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };

        var amount = await _orders.GetHeaderAmountAsync(body.DocKey, cancellationToken).ConfigureAwait(false);
        var found = ApprovalAmountGate.RequireHeaderAmount(amount);
        if (!found.Allowed)
            return new JsonResult(new { ok = false, error = found.ErrorMessage ?? "Document not found." }, JsonCamel) { StatusCode = StatusCodes.Status404NotFound };

        var approverEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value?.Trim();
        var approverLevel = (int?)AppLevel ?? 0;
        HeaderStatusChangeResult? changeResult = null;

        if (string.Equals(normalized, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            var slots = await _orders.GetApprovalSlotsAsync(body.DocKey, cancellationToken).ConfigureAwait(false)
                        ?? new PoApprovalSlots();
            var decision = await _authority.AuthorizePartialApproveAsync(User, amount.Value, slots, cancellationToken)
                .ConfigureAwait(false);
            if (!decision.Allowed)
                return new JsonResult(new { ok = false, error = decision.ErrorMessage ?? "Not allowed." }, JsonCamel) { StatusCode = StatusCodes.Status403Forbidden };

            var (okApprove, errApprove, result) = await _orders.TrySetHeaderListStatusAsync(
                body.DocKey, normalized, approverEmail, approverLevel, cancellationToken).ConfigureAwait(false);
            if (!okApprove)
                return new JsonResult(new { ok = false, error = errApprove ?? "Update failed." }, JsonCamel) { StatusCode = StatusCodes.Status409Conflict };

            changeResult = result;
            return new JsonResult(new
            {
                ok = true,
                completedLevels = changeResult?.CompletedLevels ?? decision.CompletedLevels,
                requiredLevels = changeResult?.RequiredLevels ?? decision.RequiredLevels,
                isFinal = changeResult?.IsFinal ?? decision.IsFinalApproval,
                message = changeResult?.Message ?? decision.SuccessMessage
            }, JsonCamel);
        }

        var decisionOther = await _authority.AuthorizeListStatusChangeAsync(User, amount.Value, cancellationToken).ConfigureAwait(false);
        if (!decisionOther.Allowed)
            return new JsonResult(new { ok = false, error = decisionOther.ErrorMessage ?? "Not allowed." }, JsonCamel) { StatusCode = StatusCodes.Status403Forbidden };

        var (ok, err, _) = await _orders.TrySetHeaderListStatusAsync(body.DocKey, normalized, approverEmail, 0, cancellationToken).ConfigureAwait(false);
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

    private static bool TryNormalizeListStatus(string? raw, out string normalized)
    {
        normalized = "";
        var s = (raw ?? "").Trim();
        if (s.Equals("Pending", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Pending";
            return true;
        }

        if (s.Equals("Approved", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Approved";
            return true;
        }

        if (s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Cancelled";
            return true;
        }

        if (s.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Rejected";
            return true;
        }

        return false;
    }
}
