using System.Globalization;
using static BusGo.Services.UiText;

namespace BusGo.Models;

/// <summary>
/// The exact database literals used by <c>Tickets.Status</c> and
/// <c>Tickets.PaymentStatus</c>. They are CHECK-constrained in SQL Server and
/// therefore stay Vietnamese; the UI renders them through the status converters.
/// </summary>
public static class TicketStatuses
{
    /// <summary><c>Tickets.Status</c>: seat held, payment outstanding.</summary>
    public const string Reserved = "Đã đặt";

    /// <summary><c>Tickets.Status</c> and <c>Tickets.PaymentStatus</c>: settled.</summary>
    public const string Paid = "Đã thanh toán";

    /// <summary><c>Tickets.Status</c>: seat released.</summary>
    public const string Cancelled = "Đã hủy";

    /// <summary><c>Tickets.Status</c>: boarded or trip departed.</summary>
    public const string Used = "Đã sử dụng";

    /// <summary><c>Tickets.PaymentStatus</c>: nothing collected yet.</summary>
    public const string Unpaid = "Chưa thanh toán";

    /// <summary><c>Tickets.PaymentStatus</c>: money returned to the customer.</summary>
    public const string Refunded = "Hoàn tiền";


}
public sealed record BusCompanyItem(
    int CompanyId,
    string CompanyName,
    string Hotline,
    decimal Rating,
    int ReviewCount,
    string OperatingArea,
    string Description,
    string Amenities,
    string LogoText,
    string BrandColor,
    decimal MinPrice = 120000m,
    int ActiveTripsCount = 0)
{
    public string RatingDisplay => Format("★ {0:F1} / {1:F1}", "★ {0:F1} / {1:F1}", Rating, 5m);
    public string ReviewCountDisplay => Format("({0:N0} lượt đánh giá)", "({0:N0} reviews)", ReviewCount);
    public string MinPriceDisplay => Format("Giá chỉ từ {0:N0} đ", "From {0:N0} VND", MinPrice);
    public bool HasActiveTrips => ActiveTripsCount > 0;
    public string TripStatusDisplay => ActiveTripsCount > 0 ? Format("● {0} chuyến đang chạy", "● {0} active trips", ActiveTripsCount) : T("○ Tạm hết chuyến", "○ No active trips");
    public IReadOnlyList<string> AmenityList
    {
        get
        {
            var amenities = (Amenities ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < amenities.Length; i++)
                amenities[i] = Value(amenities[i]);
            return amenities;
        }
    }
}

public sealed record TripSearchResult(
    int TripId,
    string Origin,
    string Destination,
    string BusType,
    DateTime DepartureTime,
    DateTime ArrivalTime,
    decimal Price,
    int AvailableSeats,
    string CompanyName = "Sao Việt",
    string BrandColor = "#0F4C81",
    string LogoText = "SV",
    string IntermediateStops = "",
    string DriverName = "",
    string DriverPhone = "",
    string LicensePlate = "")
{
    public string Route => $"{Origin} → {Destination}";
    public string RouteWithStops => string.IsNullOrWhiteSpace(IntermediateStops)
        ? Route
        : Format("{0} (Dừng: {1})", "{0} (Stops: {1})", Route, IntermediateStops);
    private DateTime DepartureLocal => DepartureTime.Kind == DateTimeKind.Utc ? DepartureTime.ToLocalTime() : DepartureTime;
    private DateTime ArrivalLocal => ArrivalTime.Kind == DateTimeKind.Utc ? ArrivalTime.ToLocalTime() : ArrivalTime;
    private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => dt
    };

    public string Schedule => $"{DepartureLocal:g} - {ArrivalLocal:t}";
    public string PriceDisplay => Format("{0:N0} đ", "{0:N0} VND", Price);
    public string FormattedFare => Format("{0:N0} đ / vé", "{0:N0} VND / ticket", Price);
    public string AvailableSeatsDisplay => AvailableSeats == 1 ? T("Còn 1 chỗ", "1 seat available") : Format("Còn {0} chỗ", "{0} seats available", AvailableSeats);
    public bool IsAvailable => AvailableSeats > 0 && EnsureUtc(DepartureTime) > DateTime.UtcNow;
    public double CardOpacity => IsAvailable ? 1.0 : 0.45;
    public string StatusBadgeText => EnsureUtc(DepartureTime) <= DateTime.UtcNow ? T("Đã khởi hành", "Departed") : (AvailableSeats > 0 ? Format("● Còn {0} chỗ", "● {0} seats available", AvailableSeats) : T("Hết chỗ", "Sold out"));

    public IReadOnlyList<string> StopsList =>
        new[] { Origin }
            .Concat(string.IsNullOrWhiteSpace(IntermediateStops)
                ? Array.Empty<string>()
                : IntermediateStops.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(new[] { Destination })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record SeatInfo(int SeatId, int BusId, string SeatNumber, bool IsBooked)
{
    public string DisplayName => IsBooked ? Format("{0} (đã đặt)", "{0} (booked)", SeatNumber) : SeatNumber;
}

public sealed record TicketDetails(
    int TicketId,
    string TicketCode,
    string Route,
    DateTime DepartureTime,
    DateTime ArrivalTime,
    string BusType,
    string SeatNumbers,
    decimal Price,
    string Status,
    DateTime BookingTime,
    string Origin = "",
    string Destination = "",
    decimal DistanceKm = 0)
{
    /// <summary>Number of seats in this booking (≥ 1).</summary>
    public int SeatCount { get; init; } = 1;

    public decimal BaseFare => Price * SeatCount;
    public decimal DiscountAmount { get; init; }
    public int? MembershipDiscountId { get; init; }
    public string? DiscountName { get; init; }
    public MemberTier? MembershipTier { get; init; }

    /// <summary>Saved amount due for all seats, after the booking's membership discount.</summary>
    public decimal TotalPrice => BaseFare - DiscountAmount;

    public string PassengerName { get; init; } = "";
    public string? PassengerPhone { get; init; }
    public string LicensePlate { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string DriverName { get; init; } = "";
    public string CompanyHotline { get; init; } = "";
    public string PaymentMethod { get; init; } = "";
    public decimal RefundAmount { get; init; }

    /// <summary>Raw <c>Tickets.PaymentStatus</c> value; render via <c>PaymentStatusToDisplay</c>.</summary>
    public string PaymentStatus { get; init; } = TicketStatuses.Unpaid;

    /// <summary>Deadline for an unpaid hold; null once the ticket is settled or closed.</summary>
    public DateTime? PaymentExpiresAt { get; init; }

    /// <summary>Set when the ticket was checked in or its trip departed.</summary>
    public DateTime? UsedAt { get; init; }

    public DateTime? CancelledAt { get; init; }

    public bool IsPaid => PaymentStatus == TicketStatuses.Paid;

    public bool IsUsed => Status == TicketStatuses.Used;

    public bool IsCancelled => Status == TicketStatuses.Cancelled;

    /// <summary>True while the hold is still awaiting payment and has not expired.</summary>
    public bool IsAwaitingPayment =>
        Status == TicketStatuses.Reserved && PaymentStatus == TicketStatuses.Unpaid &&
        (PaymentExpiresAt is null || PaymentExpiresAt > DateTime.UtcNow);

    private DateTime DepartureLocal => DepartureTime.Kind == DateTimeKind.Utc ? DepartureTime.ToLocalTime() : DepartureTime;
    private DateTime ArrivalLocal => ArrivalTime.Kind == DateTimeKind.Utc ? ArrivalTime.ToLocalTime() : ArrivalTime;
    private DateTime BookingLocal => BookingTime.Kind == DateTimeKind.Utc ? BookingTime.ToLocalTime() : BookingTime;
    private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => dt
    };

    /// <summary>A refund can only be requested for a settled ticket before departure.</summary>
    public bool CanRequestRefund =>
        Status == TicketStatuses.Paid && PaymentStatus == TicketStatuses.Paid && EnsureUtc(DepartureTime) > DateTime.UtcNow;

    public string DepartureFormatted => DepartureLocal.ToString("g", CultureInfo.CurrentCulture);
    public string ArrivalFormatted => ArrivalLocal.ToString("g", CultureInfo.CurrentCulture);
    public string BookingTimeFormatted => BookingLocal.ToString("g", CultureInfo.CurrentCulture);
}

public sealed record PopularRouteItem(
    int RouteId,
    string From,
    string To,
    string Duration,
    string Price,
    string Tag,
    decimal MinPrice,
    string CompanyName = "Sao Việt",
    string BrandColor = "#0F4C81",
    string LogoText = "SV",
    string IntermediateStops = "")
{
    public string RouteDisplay => $"{From} → {To}";
    public string StopsSummary => string.IsNullOrWhiteSpace(IntermediateStops)
        ? T("Tuyến chạy thẳng cao tốc", "Direct expressway route")
        : Format("Dừng: {0}", "Stops: {0}", IntermediateStops);
    public string PriceDisplay => Format("{0:N0} đ", "{0:N0} VND", MinPrice);
    public string TagDisplay => Value(Tag);
    public string DurationDisplay
    {
        get
        {
            var duration = Duration.AsSpan();
            if (duration.IsEmpty || duration[^1] != 'm')
                return Duration;
            var separator = duration.IndexOf("h ", StringComparison.Ordinal);
            if (separator >= 0 &&
                int.TryParse(duration[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) &&
                int.TryParse(duration[(separator + 2)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                return Format("{0} giờ {1} phút", "{0} hr {1} min", hours, minutes);
            if (separator < 0 && int.TryParse(duration[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes))
                return Format("{0} phút", "{0} min", minutes);
            return Duration;
        }
    }
}

public sealed record RouteSummaryItem(
    int RouteId,
    string Origin,
    string Destination,
    decimal? DistanceKm);

