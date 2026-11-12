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
            // No tenant bootstrap yet — keep app usable; page policies still apply.
            return true;
        }

        var isMaintenance = user.IsInRole(ApprovalRoles.Maintenance);
        var modules = await _tenantResolver
            .GetTenantDashboardModulesForRoleAsync(tenantCode, isMaintenance, cancellationToken)
            .ConfigureAwait(false);

        if (modules is null)
        {
            // Transient tenant-config miss must not blank the product (Dashboard regression).
            _logger.LogWarning(
                "Module config unavailable for {Tenant}; allowing {Path} until config loads.",
                tenantCode,
                path.Value);
            return true;
        }

        // Explicit false hides the module; null keeps historical default (enabled).
        return module switch
        {
            "purchaseApproval" => modules.PurchaseApproval ?? true,
            "salesApproval" => modules.SalesApproval ?? true,
            "scanPo" => modules.ScanPo ?? true,
            "receivedGoods" => modules.ReceivedGoods ?? true,
            "scanSo" => modules.ScanSo ?? true,
            "deliveryOrders" => modules.DeliveryOrders ?? true,
            // Hidden for now — re-enable via tenant maintenanceScanner when ready.
            "maintenanceScanner" => false,
            _ => true
        };
    }

    public async Task<DashboardModuleFlags> GetDashboardModulesAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        // Defaults all true — empty Overview only when tenant explicitly disables every module.
        // Maintenance Scanner tile/nav hidden for now.
        var defaults = new DashboardModuleFlags { MaintenanceScanner = false };

        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        if (tenantCode.Length == 0)
            return defaults;

        var isMaintenance = user.IsInRole(ApprovalRoles.Maintenance);
        var modules = await _tenantResolver
            .GetTenantDashboardModulesForRoleAsync(tenantCode, isMaintenance, cancellationToken)
            .ConfigureAwait(false);

        if (modules is null)
        {
            _logger.LogWarning(
                "Dashboard module config unavailable for {Tenant}; showing default modules.",
                tenantCode);
            return defaults;
        }

        return new DashboardModuleFlags
        {
            PurchaseApproval = modules.PurchaseApproval ?? true,
            SalesApproval = modules.SalesApproval ?? true,
            ScanPo = modules.ScanPo ?? true,
            ReceivedGoods = modules.ReceivedGoods ?? true,
            ScanSo = modules.ScanSo ?? true,
            DeliveryOrders = modules.DeliveryOrders ?? true,
            MaintenanceScanner = false
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
