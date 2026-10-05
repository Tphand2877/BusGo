using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static BusGo.Services.UiText;

namespace BusGo.Pages.Customer;

[Authorize]
public sealed class TicketModel : CustomerPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Confirm { get; set; }
    [BindProperty] public bool Confirmed { get; set; }
    [BindProperty] public string PaymentMethod { get; set; } = "BankTransfer";
    public TicketDetails? Ticket { get; private set; }
    public bool CanCancel => Ticket is { Status: TicketStatuses.Reserved } ticket && ticket.DepartureTime > DateTime.UtcNow;
    public bool CanEmail => ExternalApiServices.IsEmailConfigured && !string.IsNullOrWhiteSpace(CurrentUser.Account?.Email);
    public string EmailNote => !ExternalApiServices.IsEmailConfigured ? T("Cài đặt gửi email chưa được cấu hình trên hệ thống này.", "Email delivery has not been configured on this system.") : string.IsNullOrWhiteSpace(CurrentUser.Account?.Email) ? T("Thêm email vào hồ sơ để nhận vé.", "Add an email address to your profile to receive tickets.") : Format("Gửi vé PDF đến {0}.", "Send the PDF ticket to {0}.", CurrentUser.Account.Email);
    public string? MapUrl => Ticket is { } ticket && !string.IsNullOrWhiteSpace(ticket.Origin) && !string.IsNullOrWhiteSpace(ticket.Destination) ? ExternalApiServices.GetRouteMapUrl(ticket.Origin, ticket.Destination) : null;

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            Ticket = await FindOwnedTicketAsync(Id);
            if (Ticket is null) return NotFound();
        }
        catch (Exception ex) { DatabaseError(ex, "Loading ticket detail failed."); }
        return Page();
    }

    private async Task<IActionResult> DownloadAsync(string format)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var ticket = await FindOwnedTicketAsync(Id);
            if (ticket is null) return NotFound();
            if (!ticket.IsPaid) return Forbid();
            return format switch
            {
                "pdf" => File(TicketPrintService.GeneratePdf(ticket), "application/pdf", $"BusGo-{ticket.TicketCode}.pdf"),
                "png" => File(TicketPrintService.GeneratePng(ticket), "image/png", $"BusGo-{ticket.TicketCode}.png"),
                _ => File(TicketPrintService.GenerateQrCodePng(ticket.TicketCode), "image/png")
            };
        }
        catch (Exception ex)
        {
            DatabaseError(ex, "Generating ticket download failed.");
            TempData["Error"] = Error;
            return RedirectToPage(new { Id });
        }
    }

    public Task<IActionResult> OnGetPdfAsync() => DownloadAsync("pdf");
    public Task<IActionResult> OnGetPngAsync() => DownloadAsync("png");
    public Task<IActionResult> OnGetQrAsync() => DownloadAsync("qr");

    public async Task<IActionResult> OnPostEmailAsync()
    {
        try
        {
            var ticket = await FindOwnedTicketAsync(Id);
            if (ticket is null) return NotFound();
            if (!ticket.IsPaid) return Forbid();
            if (!CanEmail) TempData["Error"] = EmailNote;
            else
            {
                await ExternalApiServices.SendTicketEmailAsync(ticket, CurrentUser.Account!.Email!, HttpContext.RequestAborted);
                TempData["Success"] = T("Vé PDF đã được gửi đến địa chỉ email trong hồ sơ của bạn.", "Your PDF ticket was sent to your profile email address.");
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Sending customer ticket email failed.", ex);
            TempData["Error"] = T("Không thể gửi vé qua email. Vui lòng kiểm tra địa chỉ trong hồ sơ hoặc liên hệ hỗ trợ.", "The ticket could not be emailed. Please check your profile address or contact support.");
        }
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostCancelAsync() => await ChangeTicketAsync(false);
    public async Task<IActionResult> OnPostRefundAsync() => await ChangeTicketAsync(true);

    private async Task<IActionResult> ChangeTicketAsync(bool refund)
    {
        try
        {
            Ticket = await FindOwnedTicketAsync(Id);
            if (Ticket is null) return NotFound();
            if (!Confirmed || (refund ? !Ticket.CanRequestRefund : !CanCancel))
            {
                TempData["Error"] = T("Thao tác này không khả dụng hoặc chưa được xác nhận.", "This action is not available or has not been confirmed.");
                return RedirectToPage(new { Id });
            }
            var result = refund ? await DatabaseHelper.RequestRefundAsync(Id, AccountId) : await DatabaseHelper.CancelTicketAsync(Id, AccountId);
            TempData[result.Success ? "Success" : "Error"] = result.Success
                ? refund ? T("Yêu cầu hoàn tiền đã được gửi. Nhà vận hành phải phê duyệt hoàn tiền.", "Your refund request was submitted. An operator must approve the refund.") : T("Đặt chỗ đã được hủy và ghế đã được giải phóng.", "Your reservation was cancelled and its seats released.")
                : result.Error ?? T("Không thể cập nhật vé.", "The ticket could not be updated.");
        }
        catch (Exception ex)
        {
            DatabaseError(ex, "Changing customer ticket failed.");
            TempData["Error"] = Error;
        }
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostPaymentAsync()
    {
        try
        {
            var ticket = await FindOwnedTicketAsync(Id);
            if (ticket is null) return NotFound();
            if (!ticket.IsAwaitingPayment || PaymentMethod is not ("BankTransfer" or "Cash")) return BadRequest();
            var result = await PaymentService.CreatePaymentAsync(new PaymentRequest(ticket.TicketId, ticket.TicketCode, PaymentMethod));
            TempData[result.IsSuccess ? "Success" : "Error"] = result.IsSuccess ? T("Hướng dẫn thanh toán đã sẵn sàng. Vé chỉ được thanh toán khi nhà vận hành xác nhận đã nhận tiền.", "Payment instructions are ready. Your ticket is not paid until the operator confirms receipt.") : result.ErrorMessage ?? T("Không thể bắt đầu thanh toán.", "Payment could not be started.");
        }
        catch (Exception ex)
        {
            DatabaseError(ex, "Starting customer ticket payment failed.");
            TempData["Error"] = Error;
        }
        return RedirectToPage(new { Id });
    }

    public async Task<IActionResult> OnPostTransferAsync()
    {
        try
        {
            var ticket = await FindOwnedTicketAsync(Id);
            if (ticket is null) return NotFound();
            if (!ticket.IsAwaitingPayment || ticket.PaymentMethod != "BankTransfer") return BadRequest();
            TempData["Success"] = T("Nếu đã chuyển khoản, hãy giữ biên lai ngân hàng. Nhà vận hành sẽ xác minh thanh toán. Xác nhận này không đánh dấu vé đã thanh toán hoặc gia hạn giữ chỗ.", "If you completed the transfer, keep your bank receipt. The operator will verify the payment. This acknowledgement does not mark your ticket as paid or extend the hold.");
        }
        catch (Exception ex)
        {
            DatabaseError(ex, "Checking customer transfer failed.");
            TempData["Error"] = Error;
        }
        return RedirectToPage(new { Id });
    }
}
