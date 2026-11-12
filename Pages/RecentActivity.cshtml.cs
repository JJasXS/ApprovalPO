using ApprovalPO.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class RecentActivityModel : PageModel
{
    private readonly DashboardOverviewService _overview;

    public RecentActivityModel(DashboardOverviewService overview)
    {
        _overview = overview;
    }

    public IReadOnlyList<DashboardActivityItem> Items { get; private set; } = Array.Empty<DashboardActivityItem>();
    public bool Allowed { get; private set; }
    public string? LoadError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (modules, items) = await _overview
                .LoadRecentActivityAsync(User, DashboardOverviewService.RecentActivityPageLimit, cancellationToken)
                .ConfigureAwait(false);
            Allowed = modules.PurchaseApproval || modules.SalesApproval;
            Items = items;
            if (!Allowed)
                LoadError = "Purchase and Sales approvals are not enabled for this account.";
        }
        catch (Exception ex)
        {
            LoadError = Helpers.CatalogPageErrors.Format(ex, "recent activity");
        }
    }

    public static string ActivityTone(string status) => DashboardModel.ActivityTone(status);
}
