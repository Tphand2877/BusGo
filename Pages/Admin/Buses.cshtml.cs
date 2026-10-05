using System.ComponentModel.DataAnnotations;
using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Mvc;
using static BusGo.Services.UiText;
namespace BusGo.Pages.Admin;

public sealed class BusesModel : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Company { get; set; }
    [BindProperty] public List<BusForm> Vehicles { get; set; } = [new()];
    public IReadOnlyList<BusAdminItem> Buses { get; private set; } = [];
    public IReadOnlyList<RouteAdminItem> Routes { get; private set; } = [];
    public IReadOnlyList<string> Companies { get; private set; } = [];
    public bool Editing => Vehicles.Any(v => v.Id > 0);
    public sealed class BusForm
    {
        public int Id { get; set; }
        [Display(Name = nameof(CompanyName)), Required(ErrorMessage = "The {0} field is required."), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string CompanyName { get; set; } = "";
        [Display(Name = nameof(Hotline)), StringLength(20, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? Hotline { get; set; }
        [Display(Name = nameof(OperatingArea)), StringLength(200, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? OperatingArea { get; set; }
        public string? Description { get; set; }
        [Display(Name = nameof(Amenities)), StringLength(500, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? Amenities { get; set; }
        [Display(Name = nameof(LicensePlate)), Required(ErrorMessage = "The {0} field is required."), StringLength(20, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string LicensePlate { get; set; } = "";
        [Display(Name = nameof(SeatCount)), Range(1, 200, ErrorMessage = "The field {0} must be between {1} and {2}.")] public int SeatCount { get; set; } = 40;
        [Display(Name = nameof(BusType)), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? BusType { get; set; }
        [Display(Name = nameof(DriverName)), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? DriverName { get; set; }
        [Display(Name = nameof(DriverPhone)), StringLength(20, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? DriverPhone { get; set; }
        [Display(Name = nameof(DepartureTimeNote)), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? DepartureTimeNote { get; set; }
        public int? RouteId { get; set; }
        [Display(Name = nameof(NewOrigin)), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? NewOrigin { get; set; }
        [Display(Name = nameof(NewDestination)), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? NewDestination { get; set; }
        [Display(Name = nameof(IntermediateStops)), StringLength(500, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? IntermediateStops { get; set; }
        [Display(Name = nameof(Make)), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? Make { get; set; }
        [Display(Name = nameof(Model)), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? Model { get; set; }
        public int ManufactureYear { get; set; } = DateTime.Today.Year;
        [Display(Name = nameof(Color)), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? Color { get; set; }
        [Display(Name = nameof(RegistrationNumber)), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? RegistrationNumber { get; set; }
        [Display(Name = nameof(InspectionNumber)), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? InspectionNumber { get; set; }
        public DateTime? RegistrationDate { get; set; }
        public DateTime? InspectionExpiryDate { get; set; }
        public DateTime? InsuranceExpiryDate { get; set; }
        [Display(Name = nameof(RegisteredEntity)), StringLength(150, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? RegisteredEntity { get; set; }
        [Display(Name = nameof(OperationType)), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string? OperationType { get; set; } = "Tuyến cố định";
        public decimal? FarePrice { get; set; }
        public decimal? StagePrice { get; set; }
        public BusRegistrationInput ToInput() => new(CompanyName.Trim(), DriverName?.Trim() ?? "Chưa phân công",
            DriverPhone?.Trim() ?? "—", LicensePlate.Trim(), DepartureTimeNote?.Trim() ?? "Theo biểu đồ bến", RouteId,
            IntermediateStops?.Trim() ?? "", SeatCount, BusType?.Trim() ?? "", Make?.Trim() ?? "", Model?.Trim() ?? "",
            ManufactureYear, Color?.Trim() ?? "", RegistrationNumber?.Trim() ?? LicensePlate.Trim(), InspectionNumber?.Trim() ?? "",
            RegistrationDate, InspectionExpiryDate, InsuranceExpiryDate, RegisteredEntity?.Trim() ?? "", OperationType?.Trim() ?? "Tuyến cố định",
            FarePrice > 0 ? FarePrice : null, Hotline?.Trim(), OperatingArea?.Trim(), Description?.Trim(), Amenities?.Trim(), StagePrice > 0 ? StagePrice : null);
    }
    private Task LoadAsync() => LoadSafely(async () =>
    {
        var buses = await AdminDatabaseService.GetBusesAsync();
        Buses = buses.Where(b => (string.IsNullOrWhiteSpace(Company) || string.Equals(b.CompanyName, Company, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(Search) || b.LicensePlate.Contains(Search, StringComparison.OrdinalIgnoreCase) || b.RouteName.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
            b.DriverName.Contains(Search, StringComparison.OrdinalIgnoreCase) || b.BusType.Contains(Search, StringComparison.OrdinalIgnoreCase))).ToList();
        Routes = await AdminDatabaseService.GetRoutesAsync();
        Companies = await AdminDatabaseService.GetCompaniesListAsync();
    });
    private static async Task PopulateCompanyAsync(BusForm form, string companyName)
    {
        form.CompanyName = companyName;
        var company = await DatabaseHelper.GetBusCompanyByNameAsync(companyName);
        if (company is null) return;
        form.Hotline = company.Hotline; form.OperatingArea = company.OperatingArea;
        form.Description = company.Description; form.Amenities = company.Amenities;
    }
    public async Task<IActionResult> OnGetAsync(int? editId, string? nextCompany)
    {
        await LoadAsync();
        if (!Loaded) return Page();
        try
        {
            if (editId.HasValue)
            {
                var b = (await AdminDatabaseService.GetBusesAsync()).FirstOrDefault(b => b.BusId == editId);
                if (b is null) return NotFound();
                var form = new BusForm { Id = b.BusId, LicensePlate = b.LicensePlate, CompanyName = b.CompanyName, DriverName = b.DriverName,
                    DriverPhone = b.DriverPhone, SeatCount = b.SeatCount, BusType = b.BusType, DepartureTimeNote = b.DepartureTimeNote,
                    RouteId = Routes.FirstOrDefault(r => r.DisplayName == b.RouteName)?.RouteId, IntermediateStops = b.IntermediateStops,
                    Make = b.Make, Model = b.Model, ManufactureYear = b.ManufactureYear, Color = b.Color, RegistrationNumber = b.RegistrationNumber,
                    InspectionNumber = b.InspectionNumber, RegistrationDate = b.RegistrationDate, InspectionExpiryDate = b.InspectionExpiryDate,
                    InsuranceExpiryDate = b.InsuranceExpiryDate, RegisteredEntity = b.RegisteredEntity, OperationType = b.OperationType,
                    FarePrice = b.FullPrice, StagePrice = b.StagePrice };
                await PopulateCompanyAsync(form, b.CompanyName);
                Vehicles = [form];
            }
            else if (!string.IsNullOrWhiteSpace(nextCompany)) await PopulateCompanyAsync(Vehicles[0], nextCompany);
        }
        catch (Exception ex) { ReportFailure(ex); }
        return Page();
    }
    public async Task<IActionResult> OnPostAddDraftAsync()
    {
        if (Editing) return BadRequest();
        var previous = Vehicles.LastOrDefault();
        Vehicles.Add(new BusForm { CompanyName = previous?.CompanyName ?? "", Hotline = previous?.Hotline, OperatingArea = previous?.OperatingArea,
            Description = previous?.Description, Amenities = previous?.Amenities, RegisteredEntity = previous?.RegisteredEntity });
        ModelState.Clear();
        await LoadAsync();
        return Page();
    }
    public async Task<IActionResult> OnPostRemoveDraftAsync(int index)
    {
        if (Editing) return BadRequest();
        if (index >= 0 && index < Vehicles.Count) Vehicles.RemoveAt(index);
        if (Vehicles.Count == 0) Vehicles.Add(new());
        ModelState.Clear();
        await LoadAsync();
        return Page();
    }
    public async Task<IActionResult> OnPostCompanyAsync(int index)
    {
        if (index >= 0 && index < Vehicles.Count)
        {
            try { await PopulateCompanyAsync(Vehicles[index], Vehicles[index].CompanyName ?? ""); ModelState.Clear(); }
            catch (Exception ex) { ReportFailure(ex); }
        }
        await LoadAsync();
        return Page();
    }
    public async Task<IActionResult> OnPostSaveAsync(bool continueAdding = false)
    {
        await LoadAsync();
        if (!Loaded || !ModelState.IsValid) return Page();
        if (Vehicles.Count == 0 || (Editing && Vehicles.Count != 1)) return BadRequest();
        var pending = new List<BusForm>();
        var saved = 0;
        foreach (var form in Vehicles)
        {
            try
            {
                if (form.RouteId.HasValue && !Routes.Any(r => r.RouteId == form.RouteId))
                { ModelState.AddModelError(string.Empty, Format("{0}: chọn tuyến hợp lệ.", "{0}: select a valid route.", form.LicensePlate)); pending.Add(form); continue; }
                if (!form.RouteId.HasValue && (!string.IsNullOrWhiteSpace(form.NewOrigin) || !string.IsNullOrWhiteSpace(form.NewDestination)))
                {
                    if (string.IsNullOrWhiteSpace(form.NewOrigin) || string.IsNullOrWhiteSpace(form.NewDestination))
                    { ModelState.AddModelError(string.Empty, Format("{0}: nhập cả hai đầu cho tuyến mới.", "{0}: enter both endpoints for the new route.", form.LicensePlate)); pending.Add(form); continue; }
                    var match = Routes.FirstOrDefault(r => string.Equals(r.Origin, form.NewOrigin.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(r.Destination, form.NewDestination.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        var created = await AdminDatabaseService.AddRouteAsync(form.NewOrigin, form.NewDestination, null);
                        if (!created.Success) { ModelState.AddModelError(string.Empty, created.Error ?? T("Không thể tạo tuyến.", "Route creation failed.")); pending.Add(form); continue; }
                        Routes = await AdminDatabaseService.GetRoutesAsync();
                        match = Routes.FirstOrDefault(r => string.Equals(r.Origin, form.NewOrigin.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(r.Destination, form.NewDestination.Trim(), StringComparison.OrdinalIgnoreCase));
                    }
                    if (match is null) { ModelState.AddModelError(string.Empty, T("Không thể tải tuyến mới. Tải lại và chọn tuyến trước khi lưu.", "New route could not be loaded. Refresh and select it before saving.")); pending.Add(form); continue; }
                    form.RouteId = match.RouteId;
                }
                var result = form.Id == 0 ? await AdminDatabaseService.RegisterBusAsync(form.ToInput()) : await AdminDatabaseService.UpdateBusDetailsAsync(form.Id, form.ToInput());
                if (result.Success) saved++;
                else { pending.Add(form); ModelState.AddModelError(string.Empty, $"{form.LicensePlate}: {result.Error}"); }
            }
            catch (Exception ex) { ReportFailure(ex); pending.Add(form); }
        }
        if (saved > 0) TempData["Success"] = Format("Đã lưu {0} xe. Dịch vụ đăng ký đã cập nhật ghế và lịch chạy.", "Saved {0} vehicle(s). Seats and departure schedules were maintained by the registration service.", saved);
        if (pending.Count == 0) return RedirectToPage(new { nextCompany = continueAdding ? Vehicles[0].CompanyName : null });
        var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
        Vehicles = pending;
        ModelState.Clear();
        foreach (var error in errors) ModelState.AddModelError(string.Empty, error);
        await LoadAsync();
        return Page();
    }
    public Task<IActionResult> OnPostDeleteAsync(int id) => Mutate(() => AdminDatabaseService.DeleteBusAsync(id), T("Đã xóa xe.", "Vehicle deleted."), new { Company, Search });
}
