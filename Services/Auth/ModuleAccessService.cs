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
    private readonly ILogger<ModuleAccessService> _logger;

    public ModuleAccessService(
        IConfiguration configuration,
        TenantDbConnectionResolver tenantResolver,
        ILogger<ModuleAccessService> logger)
    {
        _configuration = configuration;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    public async Task<bool> IsAllowedAsync(ClaimsPrincipal user, PathString path, CancellationToken cancellationToken = default)
    {
        if (!TryMapPathToModule(path, out var module))
            return true;

        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (tenantCode.Length == 0)
        {
            // Fail closed for module paths when tenant is required but missing.
            _logger.LogWarning("Module path {Path} denied: TenantBootstrap:TenantCode is empty.", path.Value);
            return false;
        }

        var isMaintenance = user.IsInRole(ApprovalRoles.Maintenance);
        var modules = await _tenantResolver
            .GetTenantDashboardModulesForRoleAsync(tenantCode, isMaintenance, cancellationToken)
            .ConfigureAwait(false);

        if (modules is null)
        {
            _logger.LogWarning("Module path {Path} denied: tenant module config unavailable for {Tenant}.", path.Value, tenantCode);
            return false;
        }

        // Null individual flags keep historical default (enabled); missing tenant/module pack fails closed above.
        return module switch
        {
            "purchaseApproval" => modules.PurchaseApproval ?? true,
            "salesApproval" => modules.SalesApproval ?? true,
            "scanPo" => modules.ScanPo ?? true,
            "receivedGoods" => modules.ReceivedGoods ?? true,
            "scanSo" => modules.ScanSo ?? true,
            "deliveryOrders" => modules.DeliveryOrders ?? true,
            "maintenanceScanner" => modules.MaintenanceScanner ?? true,
            _ => true
        };
    }

    public async Task<DashboardModuleFlags> GetDashboardModulesAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var empty = new DashboardModuleFlags
        {
            PurchaseApproval = false,
            SalesApproval = false,
            ScanPo = false,
            ReceivedGoods = false,
            ScanSo = false,
            DeliveryOrders = false,
            MaintenanceScanner = false
        };

        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (tenantCode.Length == 0)
            return empty;

        var isMaintenance = user.IsInRole(ApprovalRoles.Maintenance);
        var modules = await _tenantResolver
            .GetTenantDashboardModulesForRoleAsync(tenantCode, isMaintenance, cancellationToken)
            .ConfigureAwait(false);

        if (modules is null)
            return empty;

        return new DashboardModuleFlags
        {
            PurchaseApproval = modules.PurchaseApproval ?? true,
            SalesApproval = modules.SalesApproval ?? true,
            ScanPo = modules.ScanPo ?? true,
            ReceivedGoods = modules.ReceivedGoods ?? true,
            ScanSo = modules.ScanSo ?? true,
            DeliveryOrders = modules.DeliveryOrders ?? true,
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
        if (p.Equals("/ScanSO", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/ScanSODetail", StringComparison.OrdinalIgnoreCase))
        {
            module = "scanSo";
            return true;
        }
        if (p.Equals("/DeliveryOrders", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/ScanDeliveredDetail", StringComparison.OrdinalIgnoreCase))
        {
            module = "deliveryOrders";
            return true;
        }
        if (path.StartsWithSegments("/MaintenanceScanner", StringComparison.OrdinalIgnoreCase))
        {
            module = "maintenanceScanner";
            return true;
        }

        return false;
    }
}
