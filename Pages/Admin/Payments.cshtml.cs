using static BusGo.Services.UiText;
using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Mvc;
namespace BusGo.Pages.Admin;

public sealed class PaymentsModel : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    public IReadOnlyList<PaymentAdminItem> Payments { get; private set; } = [];
    public AdminPaymentStats? Stats { get; private set; }
    public Task OnGetAsync() => LoadSafely(async () =>
    {
        if (!string.IsNullOrEmpty(Status) && Status is not ("Pending" or "Success" or "Failed" or "Refunded"))
        {
            ModelState.AddModelError(nameof(Status), T("Trạng thái thanh toán không hợp lệ.", "Unknown payment status."));
            return;
        }
        Payments = await AdminDatabaseService.GetPaymentsAsync(Status, Search);
        Stats = await AdminDatabaseService.GetPaymentStatsAsync();
    });
    public Task<IActionResult> OnPostConfirmAsync(int id, string? reference, string? note)
    {
        if (CurrentUser.IsOwner)
        {
            TempData["Error"] = T("Chủ sở hữu (Owner) không có quyền duyệt thanh toán vé; thao tác đối soát thu tiền dành cho Quản trị viên / Nhân viên bến.", "The Owner cannot approve ticket settlements; payment confirmation is reserved for Administrators / Station Staff.");
            return Task.FromResult<IActionResult>(RedirectToPage(new { Status, Search }));
        }
        return Mutate(
            () => PaymentService.ConfirmManualPaymentAsync(id, AdminId, reference?.Trim(), note?.Trim()),
            T("Đã đối soát thanh toán; vé đã được thanh toán.", "Payment settled; ticket is now paid."),
            new { Status, Search });
    }
    public Task<IActionResult> OnPostRejectAsync(int id, string? note) => Mutate(async () =>
    {
        if (string.IsNullOrWhiteSpace(note)) return (false, T("Nhập lý do trước khi từ chối thanh toán.", "Enter a reason before rejecting the payment."));
        return await PaymentService.RejectManualPaymentAsync(id, AdminId, note.Trim());
    }, T("Đã từ chối thanh toán; vé đã hủy.", "Payment rejected; ticket cancelled."), new { Status, Search });
    public Task<IActionResult> OnPostRefundAsync(int id, string? note) => Mutate(
        () => PaymentService.ApproveRefundAsync(id, AdminId, note?.Trim()), T("Đã duyệt hoàn tiền.", "Refund approved."), new { Status, Search });
    public Task<IActionResult> OnPostCheckInAsync(int ticketId)
    {
        return Mutate(
            () => TicketLifecycleService.CheckInTicketAsync(ticketId, AdminId),
            T("Đã soát vé lên xe.", "Ticket checked in for boarding."),
            new { Status, Search });
    }
}
