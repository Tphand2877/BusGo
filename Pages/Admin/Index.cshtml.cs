using BusGo.Data;
using BusGo.Models;
namespace BusGo.Pages.Admin;

public sealed class IndexModel : AdminPageModel
{
    public AdminDashboardStats? Stats { get; private set; }
    public IReadOnlyList<RecentBookingAdminItem> Bookings { get; private set; } = [];
    public IReadOnlyList<TripAdminItem> Trips { get; private set; } = [];
    public IReadOnlyList<BusAdminItem> Buses { get; private set; } = [];
    public Task OnGetAsync() => LoadSafely(async () =>
    {
        Stats = await AdminDatabaseService.GetDashboardStatsAsync();
        Bookings = await AdminDatabaseService.GetRecentBookingsAsync();
        var trips = await AdminDatabaseService.GetUpcomingTripsAsync();
        var now = DateTime.UtcNow;
        Trips = trips.Where(trip => trip.DepartureTime >= now).ToArray();
        Buses = await AdminDatabaseService.GetBusesAsync();
    });
}
