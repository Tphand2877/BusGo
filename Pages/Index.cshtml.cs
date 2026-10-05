using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static BusGo.Services.UiText;

namespace BusGo.Pages;

public sealed class IndexModel : PageModel
{
    public IReadOnlyList<PopularRouteItem> PopularRoutes { get; private set; } = [];
    public IReadOnlyList<string> Origins { get; private set; } = [];
    public IReadOnlyList<string> Destinations { get; private set; } = [];
    public (int TotalTickets, int UpcomingTrips, decimal TotalSpent)? Stats { get; private set; }
    public string? Error { get; private set; }
    public MembershipOverview? Membership { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.IsInRole("Admin")) return RedirectToPage("/Admin/Index");

        try
        {
            Origins = await DatabaseHelper.GetDistinctOriginsAsync();
            Destinations = await DatabaseHelper.GetDestinationsByOriginAsync(null);
            PopularRoutes = await DatabaseHelper.GetPopularRoutesAsync(4);
            if (CurrentUser.Account is { } account)
            {
                Stats = await DatabaseHelper.GetDashboardStatsAsync(account.AccountId);
                if (User.IsInRole("Customer"))
                    Membership = await MembershipDatabaseService.GetOverviewAsync(account.AccountId);
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Loading web dashboard failed.", ex);
            Error = T("Không thể tải thông tin hành trình hoặc tài khoản từ cơ sở dữ liệu. Vui lòng thử lại sau.", "Travel or account information could not be loaded from the database. Please try again later.");
        }
        return Page();
    }
}
