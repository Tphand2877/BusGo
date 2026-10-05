using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Authorization;

namespace BusGo.Pages.Customer;

[Authorize]
public sealed class HistoryModel : CustomerPageModel
{
    public IReadOnlyList<TicketDetails> Tickets { get; private set; } = [];
    public (int TotalCompleted, decimal TotalDistanceKm)? Stats { get; private set; }
    public async Task OnGetAsync()
    {
        try
        {
            Tickets = await DatabaseHelper.GetTicketsAsync(AccountId, true);
            Stats = await DatabaseHelper.GetHistoryStatsAsync(AccountId);
        }
        catch (Exception ex) { DatabaseError(ex, "Loading travel history failed."); }
    }
}
