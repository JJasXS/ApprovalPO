using System.Security.Claims;
using ApprovalPO.Helpers;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Auth;

public interface IModuleAccessService
{
    Task<bool> IsAllowedAsync(ClaimsPrincipal user, PathString path, CancellationToken cancellationToken = default);

    /// <summary>Module cards for the home dashboard (same rules as path middleware).</summary>
    Task<DashboardModuleFlags> GetDashboardModulesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

internal sealed class ModuleAccessService : IModuleAccessService
{
    private readonly IConfiguration _configuration;
    private readonly TenantDbConnectionResolver _tenantResolver;

    public ModuleAccessService(IConfiguration configuration, TenantDbConnectionResolver tenantResolver)
    {
        _configuration = configuration;
        _tenantResolver = tenantResolver;
    }

    public async Task<bool> IsAllowedAsync(ClaimsPrincipal user, PathString path, CancellationToken cancellationToken = default)
    {
        // Non-module pages are always allowed once signed-in.
        if (!TryMapPathToModule(path, out var module))
            return true;

        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (tenantCode.Length == 0)
            return true; // no tenant config -> keep app usable

        var isMaintenance = user.IsInRole(ApprovalRoles.Maintenance);
        var modules = await _tenantResolver
            .GetTenantDashboardModulesForRoleAsync(tenantCode, isMaintenance, cancellationToken)
            .ConfigureAwait(false);

        if (modules is null)
            return true;

        return module switch
        {
            "purchaseApproval" => modules.PurchaseApproval ?? true,
            "salesApproval" => modules.SalesApproval ?? true,
            "scanPo" => modules.ScanPo ?? true,
            "receivedGoods" => modules.ReceivedGoods ?? true,
            "maintenanceScanner" => modules.MaintenanceScanner ?? true,
            _ => true
        };
    }

    public async Task<DashboardModuleFlags> GetDashboardModulesAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var flags = new DashboardModuleFlags();
        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (tenantCode.Length == 0)
            return flags;

        var isMaintenance = user.IsInRole(ApprovalRoles.Maintenance);
        var modules = await _tenantResolver
            .GetTenantDashboardModulesForRoleAsync(tenantCode, isMaintenance, cancellationToken)
            .ConfigureAwait(false);

        if (modules is null)
            return flags;

        return new DashboardModuleFlags
        {
            PurchaseApproval = modules.PurchaseApproval ?? true,
            SalesApproval = modules.SalesApproval ?? true,
            ScanPo = modules.ScanPo ?? true,
            ReceivedGoods = modules.ReceivedGoods ?? true,
            MaintenanceScanner = modules.MaintenanceScanner ?? true
        };
    }

    private static bool TryMapPathToModule(PathString path, out string module)
    {
        module = "";
        var p = path.Value ?? "";
        if (p.Equals("/PurchaseOrders", StringComparison.OrdinalIgnoreCase))
        {
            module = "purchaseApproval";
            return true;
        }
        if (p.Equals("/SalesOrders", StringComparison.OrdinalIgnoreCase))
        {
            module = "salesApproval";
            return true;
        }
        if (p.Equals("/ScanPO", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/ScanPODetail", StringComparison.OrdinalIgnoreCase))
        {
            module = "scanPo";
            return true;
        }
        if (p.Equals("/ReceivedGoods", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/ScanReceivedDetail", StringComparison.OrdinalIgnoreCase))
        {
            module = "receivedGoods";
            return true;
        }
        if (p.Equals("/MaintenanceScanner", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/MaintenanceScanner/Index", StringComparison.OrdinalIgnoreCase))
        {
            module = "maintenanceScanner";
            return true;
        }
        return false;
    }
}
