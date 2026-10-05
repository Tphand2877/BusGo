using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BusGo.Models;
using static BusGo.Services.UiText;

namespace BusGo.Services;

/// <summary>
/// Thrown when an outbound third-party call cannot be completed. The message is safe to
/// show to the user; the inner exception (when present) carries the transport detail.
/// </summary>
public sealed class ExternalServiceException : Exception
{
    public ExternalServiceException(string message) : base(message) { }
    public ExternalServiceException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Outbound integrations. Every member throws <see cref="ExternalServiceException"/> with an
/// localized, user-presentable message when the operation cannot be completed.
/// </summary>
public static class ExternalApiServices
{
    private const string BrevoEndpoint = "https://api.brevo.com/v3/smtp/email";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BusGo/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>True when an e-mail can actually be sent (transactional API key and sender configured).</summary>
    public static bool IsEmailConfigured =>
        !string.IsNullOrWhiteSpace(AppConfig.BrevoApiKey) && !string.IsNullOrWhiteSpace(AppConfig.EmailSenderAddress);

    /// <summary>Renders the actual ticket as a PDF and sends it through Brevo.</summary>
    public static async Task SendTicketEmailAsync(TicketDetails ticket, string recipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (string.IsNullOrWhiteSpace(AppConfig.BrevoApiKey))
            throw new ExternalServiceException(T("Chưa cấu hình gửi email: thiếu biến môi trường BREVO_API_KEY.", "E-mail delivery is not configured: the BREVO_API_KEY environment variable is missing."));
        if (string.IsNullOrWhiteSpace(AppConfig.EmailSenderAddress))
            throw new ExternalServiceException(T("Chưa cấu hình gửi email: thiếu biến môi trường EMAIL_SENDER_ADDRESS.", "E-mail delivery is not configured: the EMAIL_SENDER_ADDRESS environment variable is missing."));
        if (string.IsNullOrWhiteSpace(recipient))
            throw new ExternalServiceException(T("Tài khoản này chưa có địa chỉ email nhận vé.", "No recipient e-mail address is available for this account."));

        cancellationToken.ThrowIfCancellationRequested();
        var pdfBytes = TicketPrintService.GeneratePdf(ticket);
        var passengerName = string.IsNullOrWhiteSpace(ticket.PassengerName) ? T("Quý khách", "Customer") : ticket.PassengerName;
        var payload = new
        {
            sender = new { name = AppConfig.EmailSenderName, email = AppConfig.EmailSenderAddress },
            to = new[] { new { email = recipient, name = passengerName } },
            subject = Format("[BusGo] Vé xe điện tử: {0} ({1})", "[BusGo] E-ticket: {0} ({1})", ticket.TicketCode, ticket.Route),
            htmlContent = BuildTicketEmailHtml(ticket, passengerName),
            attachment = new[] { new { content = Convert.ToBase64String(pdfBytes), name = $"{T("Ve_Xe", "Ticket")}_{ticket.TicketCode}.pdf" } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BrevoEndpoint);
        request.Headers.Add("api-key", AppConfig.BrevoApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return;
            var detail = await ReadErrorMessageAsync(response, cancellationToken);
            throw new ExternalServiceException(Format("Dịch vụ email từ chối yêu cầu (HTTP {0}){1}.", "The e-mail service rejected the request (HTTP {0}){1}.", (int)response.StatusCode, detail));
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException(T("Không thể kết nối dịch vụ email. Vui lòng thử lại.", "The e-mail service could not be reached. Please try again."), ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException(T("Dịch vụ email không phản hồi kịp thời. Vui lòng thử lại.", "The e-mail service did not respond in time. Please try again."), ex);
        }
    }

    /// <summary>Returns the HTTPS driving route for a browser link, never a server process.</summary>
    public static string GetRouteMapUrl(string origin, string destination)
    {
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination))
            throw new ExternalServiceException(T("Vé không có đầy đủ điểm đi và điểm đến để mở bản đồ lộ trình.", "The ticket does not have both an origin and destination to open the route map."));
        return "https://www.google.com/maps/dir/?api=1"
            + $"&origin={Uri.EscapeDataString(origin.Trim() + ", Việt Nam")}"
            + $"&destination={Uri.EscapeDataString(destination.Trim() + ", Việt Nam")}"
            + $"&travelmode=driving&hl={Language}";
    }

    private static string BuildTicketEmailHtml(TicketDetails ticket, string passengerName)
    {
        var name = WebUtility.HtmlEncode(passengerName);
        var route = WebUtility.HtmlEncode(ticket.Route);
        var seats = WebUtility.HtmlEncode(ticket.SeatNumbers);
        var code = WebUtility.HtmlEncode(ticket.TicketCode);
        var price = Format("{0:N0} đ", "{0:N0} VND", ticket.TotalPrice);
        var status = WebUtility.HtmlEncode(Value(ticket.Status));
        return $"<div lang='{Language}' style='font-family: Arial, sans-serif; line-height: 1.6; color: #1E293B;'>"
             + $"<h2 style='color: #0F4C81;'>{T("VÉ XE ĐIỆN TỬ - BUSGO", "BUSGO E-TICKET")}</h2>"
             + Format("<p>Kính gửi <strong>{0}</strong>,</p>", "<p>Dear <strong>{0}</strong>,</p>", name)
             + Format("<p>Thông tin vé xe mã đặt chỗ <strong>{0}</strong>. Trạng thái: <strong>{1}</strong>.</p>", "<p>Your ticket has booking code <strong>{0}</strong>. Status: <strong>{1}</strong>.</p>", code, status)
             + "<table style='width: 100%; max-width: 500px; border-collapse: collapse; margin: 16px 0;'>"
             + $"<tr><td style='padding: 6px 0; color: #64748B;'>{T("Tuyến đường:", "Route:")}</td><td style='font-weight: bold;'>{route}</td></tr>"
             + $"<tr><td style='padding: 6px 0; color: #64748B;'>{T("Giờ xuất bến:", "Departure:")}</td><td style='font-weight: bold;'>{ticket.DepartureFormatted}</td></tr>"
             + $"<tr><td style='padding: 6px 0; color: #64748B;'>{T("Số ghế:", "Seats:")}</td><td style='font-weight: bold;'>{seats}</td></tr>"
             + $"<tr><td style='padding: 6px 0; color: #64748B;'>{T("Tổng tiền:", "Total:")}</td><td style='font-weight: bold; color: #0F4C81;'>{price}</td></tr>"
             + "</table>"
             + $"<p><em>{T("File PDF vé xe điện tử (kèm mã QR soát vé tại bến) đã được đính kèm trong email này.", "Your e-ticket PDF, including the QR code for boarding verification, is attached to this email.")}</em></p>"
             + $"<p>{T("Quý khách vui lòng có mặt tại bến trước giờ xe chạy 15-30 phút để xuất trình vé và lên xe.", "Please arrive at the station 15–30 minutes before departure to present your ticket and board.")}</p>"
             + "<hr style='border: none; border-top: 1px solid #E2E8F0; margin: 20px 0;'/>"
             + $"<p style='font-size: 12px; color: #94A3B8;'>BusGo · {T("Đặt vé xe khách trực tuyến", "Online bus ticket booking")}</p>"
             + "</div>";
    }

    /// <summary>
    /// Extracts Brevo's error message defensively — the body may be empty, HTML, or a JSON
    /// object without the documented fields.
    /// </summary>
    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            LoggerService.LogWarning($"Could not read the e-mail service error body: {ex.Message}");
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                message.GetString() is { Length: > 0 } text)
            {
                return $": {text}";
            }
        }
        catch (JsonException)
        {
            LoggerService.LogWarning("The e-mail service returned a non-JSON error body.");
        }

        return string.Empty;
    }

}
