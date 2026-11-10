using ApprovalPO.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalPO.ViewComponents;

public sealed class ModuleNavViewComponent : ViewComponent
{
    private readonly IModuleAccessService _modules;

    public ModuleNavViewComponent(IModuleAccessService modules)
    {
        _modules = modules;
    }

    public async Task<IViewComponentResult> InvokeAsync(string linkClass)
    {
        var flags = await _modules.GetDashboardModulesAsync(HttpContext.User).ConfigureAwait(false);
        var goodsHref = flags.ScanPo ? "/ScanPO" : "/ReceivedGoods";
        var deliveryHref = flags.ScanSo ? "/ScanSO" : "/DeliveryOrders";
        var model = new ModuleNavLinks(
            linkClass,
            flags.PurchaseApproval,
            flags.SalesApproval,
            flags.ScanPo || flags.ReceivedGoods,
            goodsHref,
            flags.ScanSo || flags.DeliveryOrders,
            deliveryHref);
        return View(model);
    }
}

public sealed record ModuleNavLinks(
    string LinkClass,
    bool ShowPurchase,
    bool ShowSales,
    bool ShowGoods,
    string GoodsHref,
    bool ShowDelivery,
    string DeliveryHref);
