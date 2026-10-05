using static BusGo.Services.UiText;
using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Mvc;
namespace BusGo.Pages.Admin;

public sealed class CheckInModel : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    public TicketDetails? Ticket { get; private set; }
    public bool CanCheckIn => Ticket is { Status: TicketStatuses.Paid, PaymentStatus: TicketStatuses.Paid, UsedAt: null };
    public async Task<IActionResult> OnGetAsync()
    {
        if (CurrentUser.IsOwner)
        {
            TempData["Error"] = T("Chức năng soát vé chỉ dành cho nhân viên bến và quản trị viên vận hành.", "Boarding check-in is reserved for station staff and operations administrators.");
            return RedirectToPage("Index");
        }
        if (string.IsNullOrWhiteSpace(Code)) return Page();
        await LoadSafely(async () =>
        {
            Ticket = await DatabaseHelper.GetTicketByCodeAsync(Code.Trim());
            if (Ticket is null) ModelState.AddModelError(nameof(Code), T("Không tìm thấy vé với mã này.", "No ticket was found with that code."));
        });
        return Page();
    }
    public Task<IActionResult> OnPostAsync()
    {
        if (CurrentUser.IsOwner)
        {
            TempData["Error"] = T("Chủ sở hữu (Owner) không trực tiếp soát vé.", "The Owner does not perform boarding check-in.");
            return Task.FromResult<IActionResult>(RedirectToPage("Index"));
        }
        return Mutate(async () =>
        {
            if (string.IsNullOrWhiteSpace(Code)) return (false, T("Nhập hoặc quét mã vé.", "Enter or scan a ticket code."));
            var ticket = await DatabaseHelper.GetTicketByCodeAsync(Code.Trim());
            if (ticket is null) return (false, T("Không tìm thấy vé.", "Ticket not found."));
            return await TicketLifecycleService.CheckInTicketAsync(ticket.TicketId, AdminId);
        }, T("Đã soát vé hành khách thành công. Vé này không thể soát lại.", "Passenger checked in successfully. This ticket cannot be checked in again."), new { Code });
    }

    public async Task<IActionResult> OnPostScanAsync([FromForm] string? code)
    {
        if (CurrentUser.IsOwner)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new CheckInResult(
                CheckInOutcome.Invalid,
                false,
                T("Chủ sở hữu (Owner) không trực tiếp soát vé.", "The Owner does not perform boarding check-in."),
                ErrorReason: "RoleForbidden"));
        }

        var result = await TicketLifecycleService.ScanAndCheckInTicketAsync(code ?? string.Empty, AdminId, Station);
        return new JsonResult(result);
    }
}
