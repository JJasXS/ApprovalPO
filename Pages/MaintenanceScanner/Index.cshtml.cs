using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApprovalPO.Authorization;
using ApprovalPO.Services.MaintenanceScanner;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages.MaintenanceScanner;

[Authorize(Policy = PolicyNames.MaintenanceAccess)]
[ValidateAntiForgeryToken]
public class IndexModel : PageModel
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMaintenanceScannerService _scanner;

    public IndexModel(IMaintenanceScannerService scanner)
    {
        _scanner = scanner;
    }

    public string LoginEmail { get; private set; } = "";

    public void OnGet()
    {
        LoginEmail = (User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "").Trim();
    }

    public async Task<IActionResult> OnPostValidateAsync([FromBody] ValidateRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Code))
        {
            return new JsonResult(new
            {
                success = false,
                cause = "EMPTY_CODE",
                message = "Scanned code is missing.",
            }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };
        }

        try
        {
            var result = await _scanner.ValidateCodeAsync(request.Code, cancellationToken).ConfigureAwait(false);
            if (!result.Exists)
            {
                return new JsonResult(new
                {
                    success = true,
                    exists = false,
                    message = "Code not found in Stock Item.",
                }, JsonCamel);
            }

            return new JsonResult(new
            {
                success = true,
                exists = true,
                description = result.Description,
                locationCode = result.LocationCode,
                location = result.LocationDescription,
                project = result.Project,
                lastScanned = result.LastScanned,
            }, JsonCamel);
        }
        catch (Exception ex)
        {
            return new JsonResult(new
            {
                success = false,
                cause = "DB_ERROR",
                message = "Database query failed.",
                detail = ex.Message,
            }, JsonCamel) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    public async Task<IActionResult> OnGetLocationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _scanner.GetLocationDescriptionsAsync(cancellationToken).ConfigureAwait(false);
            return new JsonResult(new { success = true, locations = list }, JsonCamel);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, message = ex.Message }, JsonCamel)
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    public async Task<IActionResult> OnPostInsertDetailAsync([FromBody] InsertDetailRequest? request, CancellationToken cancellationToken)
    {
        var sessionEmail = (User.FindFirst(ClaimTypes.Email)?.Value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(sessionEmail))
        {
            return new JsonResult(new { success = false, message = "Not logged in." }, JsonCamel)
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Code))
        {
            return new JsonResult(new { success = false, message = "Code is required." }, JsonCamel)
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }

        var operatorName = ResolveOperatorDisplayName(request.OperatorDisplayName, sessionEmail);

        try
        {
            await _scanner.InsertScanDetailAsync(
                new MaintenanceScanInsertRequest
                {
                    Code = request.Code,
                    LocationDescription = request.LocationDesc,
                    LocationCode = request.LocationCode,
                    OperatorDisplayName = operatorName,
                    Remark1 = request.Remark1,
                    Remark2 = request.Remark2,
                    Remark3 = request.Remark3,
                },
                operatorName,
                cancellationToken).ConfigureAwait(false);

            return new JsonResult(new { success = true }, JsonCamel);
        }
        catch (InvalidOperationException ex)
        {
            return new JsonResult(new { success = false, message = ex.Message }, JsonCamel)
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, message = "Insert failed.", detail = ex.Message }, JsonCamel)
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    private string ResolveOperatorDisplayName(string? fromClient, string sessionEmail)
    {
        var name = (fromClient ?? "").Replace("\u00A0", " ").Trim();
        if (name.Length > 120) name = name.Substring(0, 120);
        if (string.IsNullOrWhiteSpace(name))
            name = (User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = sessionEmail;
        return name;
    }

    public sealed class ValidateRequest
    {
        public string Code { get; set; } = "";
    }

    public sealed class InsertDetailRequest
    {
        public string Code { get; set; } = "";
        public string? LocationDesc { get; set; }
        public string? LocationCode { get; set; }
        public string? OperatorDisplayName { get; set; }
        public string? Remark1 { get; set; }
        public string? Remark2 { get; set; }
        public string? Remark3 { get; set; }
    }
}
