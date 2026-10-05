using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace BusGo.Pages.Customer;

public sealed class CompanyModel : CustomerPageModel
{
    [Display(Name = nameof(Name)), BindProperty(SupportsGet = true)] public string Name { get; set; } = string.Empty;
    [Display(Name = nameof(Date)), BindProperty(SupportsGet = true)] public DateTime? Date { get; set; }
    public BusCompanyItem? Company { get; private set; }
    public IReadOnlyList<TripSearchResult> Trips { get; private set; } = [];
    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(Name)) return NotFound();
        if (!ModelState.IsValid) return Page();
        try
        {
            await DatabaseHelper.EnsureBusCompaniesSyncedAsync();
            Company = await DatabaseHelper.GetBusCompanyByNameAsync(Name);
            if (Company is null) return NotFound();
            Trips = await DatabaseHelper.GetTripsByCompanyAsync(Name, Date);
        }
        catch (Exception ex) { DatabaseError(ex, "Loading company details failed."); }
        return Page();
    }
}
