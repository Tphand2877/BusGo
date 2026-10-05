using System.Globalization;
using System.ComponentModel.DataAnnotations;
using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static BusGo.Services.UiText;

namespace BusGo.Pages.Customer;

[Authorize]
public sealed class SeatsModel : CustomerPageModel
{
    [BindProperty(SupportsGet = true)] public int TripId { get; set; }
    [BindProperty] public List<int> SeatIds { get; set; } = [];
    [BindProperty, Required(ErrorMessage = "The {0} field is required."), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}."), Display(Name = nameof(PassengerName))] public string PassengerName { get; set; } = string.Empty;
    [BindProperty, StringLength(20, ErrorMessage = "The field {0} must be a string with a maximum length of {1}."), Display(Name = nameof(PassengerPhone))] public string? PassengerPhone { get; set; }
    [BindProperty] public string PaymentMethod { get; set; } = "BankTransfer";
    [BindProperty] public bool CashReceived { get; set; }
    [BindProperty] public string? QuotedTotal { get; set; }
    [BindProperty] public int? QuotedDiscountId { get; set; }
    public BookingDiscountQuote Quote { get; private set; }
    public TripSearchResult? Trip { get; private set; }
    public IReadOnlyList<SeatInfo> Seats { get; private set; } = [];
    public bool Reviewing { get; private set; }
    public string SelectedSeats => string.Join(", ", Seats.Where(s => SeatIds.Contains(s.SeatId)).Select(s => s.SeatNumber));
    public decimal Total => Quote.TotalAmount;

    private async Task<bool> LoadAsync()
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            Trip = (await DatabaseHelper.SearchTripsAsync(null, null, null)).FirstOrDefault(t => t.TripId == TripId);
            if (Trip is null) { Error = T("Chuyến này không còn nhận đặt vé.", "This departure is no longer available for booking."); return false; }
            Seats = await DatabaseHelper.GetSeatsAsync(TripId);
            Quote = await MembershipDatabaseService.GetQuoteAsync(AccountId, Trip.Price * SeatIds.Count);
            return true;
        }
        catch (Exception ex) { Trip = null; DatabaseError(ex, "Loading seat map and membership discount failed."); return false; }
    }

    public async Task OnGetAsync()
    {
        PassengerName = IsCounterStaff ? string.Empty : CurrentUser.Account?.FullName ?? string.Empty;
        PassengerPhone = IsCounterStaff ? null : CurrentUser.Account?.Phone;
        PaymentMethod = IsCounterStaff ? "CounterCash" : "BankTransfer";
        await LoadAsync();
    }

    private void ValidateBooking()
    {
        if (SeatIds.Count == 0) ModelState.AddModelError(string.Empty, T("Vui lòng chọn ít nhất một ghế.", "Select at least one seat."));
        if (SeatIds.Count != SeatIds.Distinct().Count() || SeatIds.Any(id => !Seats.Any(s => s.SeatId == id && !s.IsBooked)))
            ModelState.AddModelError(string.Empty, T("Một ghế bạn chọn không còn trống. Vui lòng chọn lại.", "One of your selected seats is unavailable. Please choose again."));
        if (Trip is not { IsAvailable: true }) ModelState.AddModelError(string.Empty, T("Chuyến này không còn nhận đặt vé.", "This departure is no longer available."));
        if (PaymentMethod is not ("Cash" or "BankTransfer") && !(IsCounterStaff && PaymentMethod == "CounterCash"))
            ModelState.AddModelError(nameof(PaymentMethod), T("Chọn phương thức thanh toán hợp lệ.", "Choose a valid payment method."));
    }

    public async Task<IActionResult> OnPostReviewAsync()
    {
        ResetCashAcknowledgement();
        if (!await LoadAsync()) return Page();
        ValidateBooking();
        Reviewing = ModelState.IsValid;
        if (Reviewing) StoreReviewedQuote();
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync()
    {
        await LoadAsync();
        ResetCashAcknowledgement();
        return Page();
    }

    private void StoreReviewedQuote()
    {
        QuotedTotal = Quote.TotalAmount.ToString(CultureInfo.InvariantCulture);
        QuotedDiscountId = Quote.DiscountId;
        ModelState.Remove(nameof(QuotedTotal));
        ModelState.Remove(nameof(QuotedDiscountId));
    }

    private void ResetCashAcknowledgement()
    {
        CashReceived = false;
        ModelState.Remove(nameof(CashReceived));
    }

    public async Task<IActionResult> OnPostBookAsync()
    {
        if (CurrentUser.Account is null) return Challenge();
        if (!await LoadAsync()) return Page();
        ValidateBooking();
        if (!ModelState.IsValid) return Page();
        if (!decimal.TryParse(QuotedTotal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quotedTotal)
            || quotedTotal != Quote.TotalAmount || QuotedDiscountId != Quote.DiscountId)
        {
            ModelState.AddModelError(string.Empty, T("Giá vé hoặc ưu đãi đã thay đổi. Vui lòng kiểm tra lại tổng tiền và xác nhận lần nữa.",
                "The fare or discount has changed. Review the updated total and confirm again."));
            Reviewing = true;
            StoreReviewedQuote();
            ResetCashAcknowledgement();
            return Page();
        }
        if (PaymentMethod == "CounterCash" && !CashReceived)
        {
            ModelState.AddModelError(nameof(CashReceived), T("Chỉ xác nhận bán vé sau khi đã thu đủ tiền mặt của khách.", "Only confirm the sale after collecting the passenger's full cash payment."));
            Reviewing = true;
            return Page();
        }
        int? createdTicketId = null;
        string? createdTicketCode = null;
        try
        {
            var booking = await DatabaseHelper.BookTicketsAsync(AccountId, TripId, SeatIds, PassengerName.Trim(), PassengerPhone?.Trim(),
                quotedTotal, QuotedDiscountId);
            if (!booking.Success || booking.Ticket is null)
            {
                ModelState.AddModelError(string.Empty, booking.Error ?? T("Không thể giữ các ghế đã chọn.", "The selected seats could not be reserved."));
                if (booking.QuoteChanged && await LoadAsync())
                {
                    Reviewing = true;
                    StoreReviewedQuote();
                    ResetCashAcknowledgement();
                }
                return Page();
            }
            var ticket = booking.Ticket;
            createdTicketId = ticket.TicketId;
            createdTicketCode = ticket.TicketCode;
            var payment = await PaymentService.CreatePaymentAsync(new PaymentRequest(ticket.TicketId, ticket.TicketCode, PaymentMethod == "CounterCash" ? "Cash" : PaymentMethod));
            if (!payment.IsSuccess)
            {
                if (IsCounterStaff)
                    TempData["Error"] = Format("Đã lập vé {0}, nhưng chưa tạo được thanh toán. Kiểm tra trạng thái và mở Quản lý thanh toán bên dưới; không lập lại vé hoặc thu tiền lần hai.", "Ticket {0} was created, but payment could not be started. Check its status and open Payment management below; do not create another ticket or collect payment again.", ticket.TicketCode);
                else
                {
                    var released = await DatabaseHelper.CancelTicketAsync(ticket.TicketId, AccountId);
                    if (!released.Success) LoggerService.LogError($"Releasing hold {ticket.TicketCode} after payment failure failed: {released.Error}");
                    TempData["Error"] = payment.ErrorMessage ?? T("Không thể bắt đầu thanh toán.", "Payment could not be started.");
                }
                return RedirectToPage("Ticket", new { id = ticket.TicketId });
            }
            if (PaymentMethod == "CounterCash")
            {
                var settlement = await PaymentService.ConfirmManualPaymentAsync(payment.PaymentId, AccountId, null,
                    $"Thu đủ tiền mặt tại quầy cho vé {ticket.TicketCode}; nhân viên đã xác nhận nhận tiền.");
                TempData[settlement.Success ? "Success" : "Error"] = settlement.Success
                    ? Format("Đã thu tiền mặt và thanh toán vé {0}. In hoặc tải vé để giao cho khách.", "Cash was collected and ticket {0} is paid. Print or download the ticket for the passenger.", ticket.TicketCode)
                    : Format("Đã lập vé {0}, nhưng chưa xác nhận được thanh toán. Kiểm tra trạng thái và mở Quản lý thanh toán bên dưới; không lập lại vé hoặc thu tiền lần hai.", "Ticket {0} was created, but payment could not be confirmed. Check its status and open Payment management below; do not create another ticket or collect payment again.", ticket.TicketCode);
                return RedirectToPage("Ticket", new { id = ticket.TicketId });
            }
            TempData["Success"] = IsCounterStaff
                ? PaymentMethod == "Cash"
                    ? T("Đã giữ ghế cho khách, chưa thanh toán. Khách trả tiền mặt trên xe; đây chưa phải vé lên xe đã thanh toán.", "Passenger seats are reserved, but not paid. The passenger pays cash on board; this is not yet a paid boarding pass.")
                    : T("Đã giữ ghế cho khách trong 15 phút. Chờ khách chuyển khoản và nhà vận hành xác nhận; vé chưa thanh toán.", "Passenger seats are held for 15 minutes. Await the passenger's bank transfer and operator confirmation; the ticket is not paid.")
                : PaymentMethod == "Cash"
                    ? T("Ghế của bạn đã được giữ. Thanh toán cho nhà xe trên xe; đây chưa phải vé lên xe đã thanh toán.", "Your seats are reserved. Pay the operator on board; this is not yet a paid boarding pass.")
                    : T("Ghế của bạn được giữ trong 15 phút. Hoàn tất chuyển khoản và chờ nhà vận hành xác nhận.", "Your seats are held for 15 minutes. Complete the bank transfer and wait for operator confirmation.");
            return RedirectToPage("Ticket", new { id = ticket.TicketId });
        }
        catch (Exception ex)
        {
            DatabaseError(ex, "Completing customer booking failed.");
            if (createdTicketId is { } ticketId)
            {
                TempData["Error"] = Format("Đã lập vé {0}, nhưng chưa kiểm tra được kết quả thanh toán. Kiểm tra trạng thái vé trước khi tiếp tục; không lập lại vé hoặc thanh toán lần hai.", "Ticket {0} was created, but the payment result could not be checked. Check the ticket status before continuing; do not create another ticket or pay again.", createdTicketCode);
                return RedirectToPage("Ticket", new { id = ticketId });
            }
            return Page();
        }
    }
}
