using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

/// <summary>
/// Placeholder for multi-company Firebird connections (SqlAccountSync-style).
/// Wiring to switch the active Acc database will land here later.
/// </summary>
public class DatabasesModel : PageModel
{
    public string? CurrentTenant { get; private set; }
    public string? CurrentHost { get; private set; }
    public string? CurrentDatabasePath { get; private set; }

    private readonly IConfiguration _configuration;

    public DatabasesModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void OnGet()
    {
        CurrentTenant = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        CurrentHost = (_configuration["TenantBootstrap:FirebirdHost"] ?? "").Trim();
        CurrentDatabasePath = (_configuration["TenantBootstrap:FirebirdDatabasePath"] ?? "").Trim();
    }
}
