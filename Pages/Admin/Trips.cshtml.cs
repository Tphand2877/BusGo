using static BusGo.Services.UiText;
using System.ComponentModel.DataAnnotations;
using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Mvc;
namespace BusGo.Pages.Admin;

public sealed class TripsModel : AdminPageModel
{
    public IReadOnlyList<RouteAdminItem> Routes { get; private set; } = [];
    public IReadOnlyList<BusAdminItem> Buses { get; private set; } = [];
    public IReadOnlyList<TripAdminItem> Trips { get; private set; } = [];
    [BindProperty] public TripForm Input { get; set; } = new();
    public sealed class TripForm
    {
        public int Id { get; set; }
        [Range(1, int.MaxValue, ErrorMessage = "The field {0} must be between {1} and {2}.")] [Display(Name = nameof(RouteId))] public int RouteId { get; set; }
        [Range(1, int.MaxValue, ErrorMessage = "The field {0} must be between {1} and {2}.")] [Display(Name = nameof(BusId))] public int BusId { get; set; }
        public DateTime Departure { get; set; } = DateTime.Now.AddDays(1);
        public DateTime Arrival { get; set; } = DateTime.Now.AddDays(1).AddHours(2);
        [Range(typeof(decimal), "0", "9999999999999999", ErrorMessage = "The field {0} must be between {1} and {2}.")] [Display(Name = nameof(Price))] public decimal Price { get; set; }
    }
    private Task LoadAsync() => LoadSafely(async () =>
    {
        Routes = (await AdminDatabaseService.GetRoutesAsync()).Where(InScope).ToList();
        Buses = await AdminDatabaseService.GetBusesAsync();
        Trips = (await AdminDatabaseService.GetTripsAsync()).Where(InScope).ToList();
    });
    public async Task<IActionResult> OnGetAsync(int? editId)
    {
        await LoadAsync();
        if (editId.HasValue && Loaded)
        {
            var t = Trips.FirstOrDefault(t => t.TripId == editId);
            if (t is null) return NotFound();
            Input = new() { Id = t.TripId, RouteId = t.RouteId, BusId = t.BusId, Departure = DateTime.SpecifyKind(t.DepartureTime, DateTimeKind.Utc).ToLocalTime(), Arrival = DateTime.SpecifyKind(t.ArrivalTime, DateTimeKind.Utc).ToLocalTime(), Price = t.Price };
        }
        return Page();
    }
    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadAsync();
        if (!Loaded || !ModelState.IsValid) return Page();
        if (!Routes.Any(r => r.RouteId == Input.RouteId) || (Input.Id != 0 && !Trips.Any(t => t.TripId == Input.Id))) return Forbid();
        return await Mutate(() => Input.Id == 0
            ? AdminDatabaseService.AddTripAsync(Input.RouteId, Input.BusId, Input.Departure, Input.Arrival, Input.Price)
            : AdminDatabaseService.UpdateTripAsync(Input.Id, Input.RouteId, Input.BusId, Input.Departure, Input.Arrival, Input.Price), T("Đã lưu chuyến.", "Trip saved."));
    }
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await LoadAsync();
        if (!Loaded) return Page();
        if (!Trips.Any(t => t.TripId == id)) return Forbid();
        return await Mutate(() => AdminDatabaseService.DeleteTripAsync(id), T("Đã xóa chuyến.", "Trip deleted."));
    }
    public async Task<IActionResult> OnPostCancelAsync(int id, string? reason)
    {
        await LoadAsync();
        if (!Loaded) return Page();
        if (!Trips.Any(t => t.TripId == id)) return Forbid();
        return await Mutate(() => AdminDatabaseService.CancelTripAsync(id, reason?.Trim()), T("Đã hủy chuyến. Vé và trạng thái thanh toán liên quan đã được cập nhật.", "Trip cancelled. Associated tickets and payment states were updated."));
    }
}
