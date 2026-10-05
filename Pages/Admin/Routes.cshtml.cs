using static BusGo.Services.UiText;
using System.ComponentModel.DataAnnotations;
using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Mvc;
namespace BusGo.Pages.Admin;

public sealed class RoutesModel : AdminPageModel
{
    public IReadOnlyList<RouteAdminItem> Routes { get; private set; } = [];
    [BindProperty] public RouteForm Input { get; set; } = new();
    public sealed class RouteForm
    {
        public int Id { get; set; }
        [Required(ErrorMessage = "The {0} field is required."), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] [Display(Name = nameof(Origin))] public string Origin { get; set; } = "";
        [Required(ErrorMessage = "The {0} field is required."), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] [Display(Name = nameof(Destination))] public string Destination { get; set; } = "";
        [Range(typeof(decimal), "0", "99999999", ErrorMessage = "The field {0} must be between {1} and {2}.")] [Display(Name = nameof(Distance))] public decimal? Distance { get; set; }
    }
    private Task LoadAsync() => LoadSafely(async () => Routes = (await AdminDatabaseService.GetRoutesAsync()).Where(InScope).ToList());
    public async Task<IActionResult> OnGetAsync(int? editId)
    {
        await LoadAsync();
        if (editId.HasValue && Loaded)
        {
            var route = Routes.FirstOrDefault(r => r.RouteId == editId);
            if (route is null) return NotFound();
            Input = new() { Id = route.RouteId, Origin = route.Origin, Destination = route.Destination, Distance = route.DistanceKm };
        }
        else Input.Origin = Station;
        return Page();
    }
    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadAsync();
        if (!Loaded || !ModelState.IsValid) return Page();
        if (Input.Id != 0 && !Routes.Any(r => r.RouteId == Input.Id)) return Forbid();
        return await Mutate(() => Input.Id == 0
            ? AdminDatabaseService.AddRouteAsync(Input.Origin, Input.Destination, Input.Distance)
            : AdminDatabaseService.UpdateRouteAsync(Input.Id, Input.Origin, Input.Destination, Input.Distance), T("Đã lưu tuyến.", "Route saved."));
    }
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await LoadAsync();
        if (!Loaded) return Page();
        if (!Routes.Any(r => r.RouteId == id)) return Forbid();
        return await Mutate(() => AdminDatabaseService.DeleteRouteAsync(id), T("Đã xóa tuyến.", "Route deleted."));
    }
}
