using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusGo.Pages.Customer;

[Authorize]
public sealed class TicketsModel : CustomerPageModel
{
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }
    public IReadOnlyList<TicketDetails> Tickets { get; private set; } = [];
    public async Task OnGetAsync()
    {
        try
        {
            var tickets = await DatabaseHelper.GetTicketsAsync(AccountId, false);
            Tickets = Filter == "pending" ? tickets.Where(t => !t.IsPaid).ToArray() : tickets;
        }
        catch (Exception ex) { DatabaseError(ex, "Loading active tickets failed."); }
    }
}
