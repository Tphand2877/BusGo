using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace BusGo.Pages.Customer;

public sealed class SearchModel : CustomerPageModel
{
    private const int PageSize = 12;

    [Display(Name = nameof(Origin)), BindProperty(SupportsGet = true)] public string? Origin { get; set; }
    [Display(Name = nameof(Destination)), BindProperty(SupportsGet = true)] public string? Destination { get; set; }
    [Display(Name = nameof(Date)), BindProperty(SupportsGet = true)] public DateTime? Date { get; set; }
    [Display(Name = nameof(Company)), BindProperty(SupportsGet = true)] public string? Company { get; set; }
    [Display(Name = nameof(BusType)), BindProperty(SupportsGet = true)] public string? BusType { get; set; }
    [Display(Name = nameof(DeparturePeriod)), BindProperty(SupportsGet = true)] public string? DeparturePeriod { get; set; }
    [Display(Name = nameof(MinimumSeats)), BindProperty(SupportsGet = true)] public int MinimumSeats { get; set; }
    [Display(Name = nameof(Sort)), BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [Display(Name = nameof(PageNumber)), BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public IReadOnlyList<TripSearchResult> Trips { get; private set; } = [];
    public IReadOnlyList<BusCompanyItem> Companies { get; private set; } = [];
    public IReadOnlyList<string> Origins { get; private set; } = [];
    public IReadOnlyList<string> Destinations { get; private set; } = [];
    public IReadOnlyList<string> BusTypes { get; private set; } = [];
    public int TotalTrips { get; private set; }
    public int TotalPages { get; private set; }

    public async Task<IActionResult> OnGetAsync(bool swap = false)
    {
        if (swap) return RedirectToPage(new
        {
            Origin = Destination,
            Destination = Origin,
            Date = Date?.ToString("yyyy-MM-dd"),
            Company,
            BusType,
            DeparturePeriod,
            MinimumSeats,
            Sort
        });

        Date ??= DateTime.Today;
        PageNumber = Math.Max(1, PageNumber);
        if (!ModelState.IsValid) return Page();
        try
        {
            Origins = await DatabaseHelper.GetDistinctOriginsAsync();
            Destinations = await DatabaseHelper.GetDestinationsByOriginAsync(null);
            Companies = await DatabaseHelper.GetBookingCompaniesAsync(Origin, Destination, Date);
            var allTrips = await DatabaseHelper.SearchTripsAsync(Origin, Destination, Date);
            BusTypes = allTrips.Select(t => t.BusType).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            IEnumerable<TripSearchResult> filtered = allTrips;
            if (!string.IsNullOrWhiteSpace(Company))
                filtered = filtered.Where(t => string.Equals(t.CompanyName, Company, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(BusType))
                filtered = filtered.Where(t => string.Equals(t.BusType, BusType, StringComparison.OrdinalIgnoreCase));
            if (MinimumSeats > 0)
                filtered = filtered.Where(t => t.AvailableSeats >= MinimumSeats);
            filtered = DeparturePeriod switch
            {
                "early" => filtered.Where(t => t.DepartureTime.ToLocalTime().Hour < 9),
                "morning" => filtered.Where(t => t.DepartureTime.ToLocalTime().Hour is >= 9 and < 12),
                "afternoon" => filtered.Where(t => t.DepartureTime.ToLocalTime().Hour is >= 12 and < 17),
                "evening" => filtered.Where(t => t.DepartureTime.ToLocalTime().Hour >= 17),
                _ => filtered
            };
            filtered = Sort switch
            {
                "price" => filtered.OrderBy(t => t.Price).ThenBy(t => t.DepartureTime),
                "seats" => filtered.OrderByDescending(t => t.AvailableSeats).ThenBy(t => t.DepartureTime),
                _ => filtered.OrderBy(t => t.DepartureTime)
            };

            var results = filtered.ToArray();
            TotalTrips = results.Length;
            TotalPages = (int)Math.Ceiling(TotalTrips / (double)PageSize);
            PageNumber = TotalPages == 0 ? 1 : Math.Min(PageNumber, TotalPages);
            Trips = results.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToArray();
        }
        catch (Exception ex) { DatabaseError(ex, "Loading trip search failed."); }
        return Page();
    }
}
