using BusGo.Services;

namespace BusGo.Common;

public static class TicketStatusText
{
    public static string ToDisplay(string? status) => DomainText.ToDisplay(status);
}

public static class PaymentStatusText
{
    public static string ToDisplay(string? status) => DomainText.ToDisplay(status);
}
