using static BusGo.Services.UiText;
using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BusGo.Pages.Admin;

[Authorize(Roles = "Admin,Owner")]
public abstract class AdminPageModel : PageModel
{
    public bool Loaded { get; protected set; }
    public string Station => LocationService.Instance.CurrentLocation;
    protected bool InScope(RouteAdminItem route) => string.IsNullOrWhiteSpace(Station) || route.Origin.Contains(Station, StringComparison.OrdinalIgnoreCase) || route.Destination.Contains(Station, StringComparison.OrdinalIgnoreCase);
    protected bool InScope(TripAdminItem trip) => string.IsNullOrWhiteSpace(Station) || trip.Route.Contains(Station, StringComparison.OrdinalIgnoreCase);
    protected async Task LoadSafely(Func<Task> load)
    {
        try { await load(); Loaded = true; }
        catch (Exception ex) { ReportFailure(ex); }
    }
    protected void ReportFailure(Exception exception)
    {
        HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(GetType()).LogError(exception, "Administrative operation failed");
        ModelState.AddModelError(string.Empty, exception is UnauthorizedAccessException
            ? T("Phiên quản trị không còn hợp lệ. Vui lòng đăng nhập lại.", "Your administrator session is no longer valid. Sign in again.")
            : T("Không thể hoàn tất thao tác. Kiểm tra kết nối cơ sở dữ liệu và thử lại. Không có kết quả thành công nào được giả định.", "The operation could not be completed. Check the database connection and retry. No successful result has been assumed."));
    }
    protected async Task<IActionResult> Mutate(Func<Task<(bool Success, string? Error)>> action, string success, object? routeValues = null)
    {
        try
        {
            var result = await action();
            TempData[result.Success ? "Success" : "Error"] = result.Success ? success : result.Error ?? T("Thao tác bị từ chối.", "The operation was refused.");
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            TempData["Error"] = T("Không thể hoàn tất thao tác. Kiểm tra kết nối cơ sở dữ liệu và thử lại.", "The operation could not be completed. Check the database connection and retry.");
        }
        return RedirectToPage(routeValues);
    }
    protected int AdminId => CurrentUser.Account?.AccountId ?? throw new UnauthorizedAccessException();
}
