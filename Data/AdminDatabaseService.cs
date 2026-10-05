using System.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.Data.SqlClient;
using static BusGo.Services.UiText;

namespace BusGo.Data;

public static class AdminDatabaseService
{
    private static void RequireAdmin()
    {
        if (!CurrentUser.IsAdmin)
            throw new UnauthorizedAccessException(T("Yêu cầu quyền quản trị viên.", "Admin access required."));
    }

    /// <summary>
    /// The station this workstation may administer. Sourced the same way the admin
    /// shell sources it, so the rule the UI states is now also enforced below the UI
    /// (AUD-D-005). Pinned by <c>STATION_LOCATION</c> where it matters; geolocation
    /// can no longer decide it.
    /// </summary>
    private static string StationScope => LocationService.Instance.CurrentLocation;

    /// <summary>
    /// A route is in scope when either endpoint names the station, matching the rule
    /// the admin screens display.
    /// </summary>
    private static bool IsWithinStationScope(string origin, string destination)
    {
        var scope = StationScope;
        return string.IsNullOrWhiteSpace(scope)
               || origin.Contains(scope, StringComparison.OrdinalIgnoreCase)
               || destination.Contains(scope, StringComparison.OrdinalIgnoreCase);
    }

    private static string OutOfScopeMessage =>
        Format("Máy trạm này chỉ được quản lý các tuyến phục vụ Bến xe {0}.", "This workstation may only administer routes serving {0} bus station.", StationScope);

    /// <summary>
    /// SQL fragment restricting a route reference to the station scope. Applied in the
    /// same statement as the write so no caller can bypass it.
    /// </summary>
    private const string RouteInStationScope =
        " AND EXISTS (SELECT 1 FROM Routes sc WHERE sc.RouteId = @Route AND (sc.Origin LIKE @Scope OR sc.Destination LIKE @Scope))";


    /// <summary>Whether a persisted route lies inside this workstation's station scope.</summary>
    private static async Task<bool> RouteIsInScopeAsync(int routeId)
    {
        const string sql = "SELECT Origin, Destination FROM Routes WHERE RouteId = @Route;";
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Route", SqlDbType.Int).Value = routeId;
        await using var reader = await command.ExecuteReaderAsync();
        // An unknown route is reported as in scope so the caller surfaces the real
        // "route is invalid" reason rather than a misleading scope refusal.
        return !await reader.ReadAsync() || IsWithinStationScope(reader.GetString(0), reader.GetString(1));
    }
    private static void AddScopeParameter(SqlCommand command) =>
        command.Parameters.Add("@Scope", SqlDbType.NVarChar, 120).Value = $"%{EscapeLikePattern(StationScope)}%";

    /// <summary>
    /// Headline figures for the admin dashboard. Revenue is taken from the payment ledger
    /// (settled minus refunded) so unpaid seat holds never inflate it; those are reported
    /// separately as awaiting settlement.
    /// </summary>
    public static async Task<AdminDashboardStats> GetDashboardStatsAsync()
    {
        RequireAdmin();
        const string sql = @"
SELECT
    (SELECT COUNT(*) FROM Routes),
    (SELECT COUNT(*) FROM Buses),
    (SELECT COUNT(*) FROM Trips WHERE DepartureTime >= SYSUTCDATETIME()),
    (SELECT COUNT(*) FROM Tickets WHERE Status IN (N'Đã thanh toán', N'Đã sử dụng')),
    (SELECT COALESCE(SUM(CASE WHEN Status = N'Success' THEN Amount ELSE 0 END), 0)
          - COALESCE(SUM(CASE WHEN Status = N'Refunded' THEN Amount ELSE 0 END), 0) FROM Payments),
    (SELECT COUNT(*) FROM Accounts WHERE Role = N'Customer' OR Role IS NULL),
    (SELECT COUNT(*) FROM Payments WHERE Status = N'Pending'),
    (SELECT COALESCE(SUM(Amount), 0) FROM Payments WHERE Status = N'Pending'),
    (SELECT COALESCE(SUM(Amount), 0) FROM Payments WHERE Status = N'Refunded');";

        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return AdminDashboardStats.Empty;
        return new AdminDashboardStats(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetDecimal(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8));
    }

    public static async Task<IReadOnlyList<RecentBookingAdminItem>> GetRecentBookingsAsync(int top = 8)
    {
        RequireAdmin();
        const string sql = @"
SELECT TOP (@Top)
    tk.TicketId, tk.TicketCode, r.Origin + N' → ' + r.Destination,
    tk.PassengerName, tk.PassengerPhone,
    COALESCE((SELECT STRING_AGG(s.SeatNumber, N', ') WITHIN GROUP (ORDER BY s.SeatId)
              FROM TicketSeats ts JOIN Seats s ON s.SeatId = ts.SeatId
              WHERE ts.TicketId = tk.TicketId), N'—'),
    tk.TotalAmount, tk.Status, tk.BookingTime
FROM Tickets tk
JOIN Trips tr ON tr.TripId = tk.TripId
JOIN Routes r ON r.RouteId = tr.RouteId
ORDER BY tk.BookingTime DESC;";

        var list = new List<RecentBookingAdminItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Top", SqlDbType.Int).Value = Math.Clamp(top, 1, 100);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new RecentBookingAdminItem(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetDecimal(6), reader.GetString(7), reader.GetDateTime(8)));
        }
        return list;
    }

    public static async Task<IReadOnlyList<TripAdminItem>> GetUpcomingTripsAsync(int top = 6)
    {
        RequireAdmin();
        const string sql = @"
SELECT TOP (@Top)
    t.TripId, t.RouteId, t.BusId, r.Origin + N' → ' + r.Destination,
    b.LicensePlate, t.DepartureTime, t.ArrivalTime, t.Price
FROM Trips t
JOIN Routes r ON r.RouteId = t.RouteId
JOIN Buses b ON b.BusId = t.BusId
ORDER BY CASE WHEN t.DepartureTime >= SYSUTCDATETIME() THEN 0 ELSE 1 END,
         t.DepartureTime ASC;";

        var list = new List<TripAdminItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Top", SqlDbType.Int).Value = Math.Clamp(top, 1, 100);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new TripAdminItem(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetDateTime(5),
                reader.GetDateTime(6), reader.GetDecimal(7)));
        }
        return list;
    }

    // ==================================================================
    // PAYMENT SETTLEMENT CONSOLE
    // ==================================================================

    /// <summary>
    /// Settlement ledger rows joined to the ticket, trip and route they belong to.
    /// Pending rows are listed first because they are the ones that need an operator.
    /// </summary>
    public static async Task<IReadOnlyList<PaymentAdminItem>> GetPaymentsAsync(string? statusFilter = null, string? search = null, int top = 200)
    {
        RequireAdmin();
        const string sql = @"
SELECT TOP (@Top)
    p.PaymentId, p.TicketId, tk.TicketCode, p.TransactionNo, p.ProviderTransactionNo,
    p.PaymentMethod, p.Provider, p.Amount, p.Status, tk.Status, tk.PassengerName,
    r.Origin + N' → ' + r.Destination, tr.DepartureTime, p.CreatedAt, p.PaidAt,
    p.RefundRequestedAt, p.RefundedAt, a.FullName, p.Notes, tk.PaymentExpiresAt
FROM Payments p
JOIN Tickets tk ON tk.TicketId = p.TicketId
JOIN Trips tr ON tr.TripId = tk.TripId
JOIN Routes r ON r.RouteId = tr.RouteId
LEFT JOIN Accounts a ON a.AccountId = p.ConfirmedByAccountId
WHERE (@Status IS NULL OR p.Status = @Status)
  AND (@Search IS NULL
       OR tk.TicketCode LIKE @Pattern
       OR p.TransactionNo LIKE @Pattern
       OR p.ProviderTransactionNo LIKE @Pattern
       OR tk.PassengerName LIKE @Pattern
       OR tk.PassengerPhone LIKE @Pattern)
ORDER BY CASE WHEN p.Status = N'Pending' THEN 0 ELSE 1 END, p.CreatedAt DESC;";

        var status = NormalizeLedgerStatus(statusFilter);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var list = new List<PaymentAdminItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Top", SqlDbType.Int).Value = Math.Clamp(top, 1, 1000);
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@Search", SqlDbType.NVarChar, 100).Value = (object?)term ?? DBNull.Value;
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 102).Value = term is null
            ? DBNull.Value
            : "%" + EscapeLikePattern(term) + "%";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new PaymentAdminItem(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetDecimal(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetDateTime(12),
                reader.GetDateTime(13),
                reader.IsDBNull(14) ? null : reader.GetDateTime(14),
                reader.IsDBNull(15) ? null : reader.GetDateTime(15),
                reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetDateTime(19)));
        }
        return list;
    }

    public static async Task<AdminPaymentStats> GetPaymentStatsAsync()
    {
        RequireAdmin();
        const string sql = "SELECT Status, COUNT(*), COALESCE(SUM(Amount), 0) FROM Payments GROUP BY Status;";

        int pendingCount = 0, successCount = 0, failedCount = 0, refundedCount = 0;
        decimal pendingAmount = 0, successAmount = 0, failedAmount = 0, refundedAmount = 0;

        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var status = reader.GetString(0);
            var count = reader.GetInt32(1);
            var amount = reader.GetDecimal(2);
            switch (status)
            {
                case "Pending": pendingCount = count; pendingAmount = amount; break;
                case "Success": successCount = count; successAmount = amount; break;
                case "Failed": failedCount = count; failedAmount = amount; break;
                case "Refunded": refundedCount = count; refundedAmount = amount; break;
            }
        }
        return new AdminPaymentStats(
            pendingCount, pendingAmount,
            successCount, successAmount,
            failedCount, failedAmount,
            refundedCount, refundedAmount);
    }

    /// <summary>
    /// Per-route revenue for the given UTC window. Only ledger rows that reached
    /// <c>Success</c> count as revenue; <c>Refunded</c> rows are subtracted.
    /// </summary>
    public static async Task<IReadOnlyList<RevenueReportItem>> GetRevenueReportAsync(DateTime fromUtc, DateTime toUtc)
    {
        RequireAdmin();
        const string sql = @"
SELECT r.Origin + N' → ' + r.Destination AS RouteSummary,
       SUM(CASE WHEN p.Status = N'Success' THEN 1 ELSE 0 END),
       COALESCE(SUM(CASE WHEN p.Status = N'Success' THEN p.Amount ELSE 0 END), 0),
       COALESCE(SUM(CASE WHEN p.Status = N'Refunded' THEN p.Amount ELSE 0 END), 0),
       COALESCE(AVG(bc.CommissionRate), 0.1000)
FROM Payments p
JOIN Tickets tk ON tk.TicketId = p.TicketId
JOIN Trips tr ON tr.TripId = tk.TripId
JOIN Routes r ON r.RouteId = tr.RouteId
JOIN Buses b ON b.BusId = tr.BusId
LEFT JOIN BusCompanies bc ON bc.CompanyName = b.CompanyName
WHERE p.Status IN (N'Success', N'Refunded')
  AND p.CreatedAt >= @From
  AND p.CreatedAt < @To
GROUP BY r.Origin, r.Destination
ORDER BY 3 DESC, RouteSummary ASC;";

        var list = new List<RevenueReportItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@From", SqlDbType.DateTime2).Value = fromUtc;
        command.Parameters.Add("@To", SqlDbType.DateTime2).Value = toUtc;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new RevenueReportItem(reader.GetString(0), reader.GetInt32(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        return list;
    }

    public static async Task<IReadOnlyList<CompanyRevenueReportItem>> GetCompanyRevenueReportAsync(DateTime fromUtc, DateTime toUtc)
    {
        RequireAdmin();
        const string sql = @"
SELECT COALESCE(b.CompanyName, N'Khác') AS CompanyName,
       COUNT(DISTINCT tr.TripId) AS TripsCount,
       SUM(CASE WHEN p.Status = N'Success' THEN 1 ELSE 0 END) AS TicketsSold,
       COALESCE(SUM(CASE WHEN p.Status = N'Success' THEN p.Amount ELSE 0 END), 0) AS GrossRevenue,
       COALESCE(SUM(CASE WHEN p.Status = N'Refunded' THEN p.Amount ELSE 0 END), 0) AS RefundedAmount,
       COALESCE(AVG(bc.CommissionRate), 0.1000) AS CommissionRate
FROM Payments p
JOIN Tickets tk ON tk.TicketId = p.TicketId
JOIN Trips tr ON tr.TripId = tk.TripId
JOIN Buses b ON b.BusId = tr.BusId
LEFT JOIN BusCompanies bc ON bc.CompanyName = b.CompanyName
WHERE p.Status IN (N'Success', N'Refunded')
  AND p.CreatedAt >= @From
  AND p.CreatedAt < @To
GROUP BY b.CompanyName
ORDER BY 4 DESC, CompanyName ASC;";

        var list = new List<CompanyRevenueReportItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@From", SqlDbType.DateTime2).Value = fromUtc;
        command.Parameters.Add("@To", SqlDbType.DateTime2).Value = toUtc;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new CompanyRevenueReportItem(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5)));
        }
        return list;
    }

    public static async Task<IReadOnlyList<PaymentChannelReportItem>> GetPaymentChannelReportAsync(DateTime fromUtc, DateTime toUtc)
    {
        RequireAdmin();
        const string sql = @"
SELECT p.PaymentMethod,
       COUNT(CASE WHEN p.Status = N'Success' THEN 1 END) AS SuccessCount,
       COALESCE(SUM(CASE WHEN p.Status = N'Success' THEN p.Amount ELSE 0 END), 0) AS SuccessAmount,
       COUNT(CASE WHEN p.Status = N'Refunded' THEN 1 END) AS RefundedCount,
       COALESCE(SUM(CASE WHEN p.Status = N'Refunded' THEN p.Amount ELSE 0 END), 0) AS RefundedAmount
FROM Payments p
WHERE p.Status IN (N'Success', N'Refunded')
  AND p.CreatedAt >= @From
  AND p.CreatedAt < @To
GROUP BY p.PaymentMethod
ORDER BY SuccessAmount DESC;";

        var list = new List<PaymentChannelReportItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@From", SqlDbType.DateTime2).Value = fromUtc;
        command.Parameters.Add("@To", SqlDbType.DateTime2).Value = toUtc;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new PaymentChannelReportItem(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetDecimal(2),
                reader.GetInt32(3),
                reader.GetDecimal(4)));
        }
        return list;
    }

    // ==================================================================
    // ROUTES
    // ==================================================================

    public static async Task<IReadOnlyList<RouteAdminItem>> GetRoutesAsync()
    {
        RequireAdmin();
        const string sql = "SELECT RouteId, Origin, Destination, DistanceKm FROM Routes ORDER BY Origin, Destination;";
        var list = new List<RouteAdminItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new RouteAdminItem(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3)));
        return list;
    }

    public static async Task<(bool Success, string? Error)> AddRouteAsync(string origin, string destination, decimal? distance)
    {
        RequireAdmin();
        origin = origin.Trim();
        destination = destination.Trim();
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination)) return (false, T("Hãy nhập cả điểm đi và điểm đến.", "Enter both an origin and a destination."));
        if (string.Equals(origin, destination, StringComparison.OrdinalIgnoreCase)) return (false, T("Điểm đi và điểm đến phải khác nhau.", "Origin and destination must be different."));
        if (distance is < 0) return (false, T("Khoảng cách không được âm.", "Distance cannot be negative."));
        if (!IsWithinStationScope(origin, destination)) return (false, OutOfScopeMessage);

        const string sql = @"
INSERT INTO Routes (Origin, Destination, DistanceKm)
SELECT @Origin, @Destination, @Distance
WHERE NOT EXISTS (SELECT 1 FROM Routes WHERE Origin = @Origin AND Destination = @Destination);";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Origin", SqlDbType.NVarChar, 100).Value = origin;
            command.Parameters.Add("@Destination", SqlDbType.NVarChar, 100).Value = destination;
            command.Parameters.Add("@Distance", SqlDbType.Decimal).Value = distance is null ? DBNull.Value : distance.Value;
            return await command.ExecuteNonQueryAsync() == 1 ? (true, null) : (false, T("Tuyến này đã tồn tại.", "That route already exists."));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"AddRouteAsync failed for {origin} -> {destination}.", ex);
            return (false, T("Không thể tạo tuyến.", "The route could not be created."));
        }
    }

    /// <summary>Full route edit. Rejects duplicates and identical origin/destination.</summary>
    public static async Task<(bool Success, string? Error)> UpdateRouteAsync(int routeId, string origin, string destination, decimal? distanceKm)
    {
        RequireAdmin();
        origin = origin.Trim();
        destination = destination.Trim();
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination)) return (false, T("Hãy nhập cả điểm đi và điểm đến.", "Enter both an origin and a destination."));
        if (string.Equals(origin, destination, StringComparison.OrdinalIgnoreCase)) return (false, T("Điểm đi và điểm đến phải khác nhau.", "Origin and destination must be different."));
        if (distanceKm is < 0) return (false, T("Khoảng cách không được âm.", "Distance cannot be negative."));
        if (!IsWithinStationScope(origin, destination)) return (false, OutOfScopeMessage);

        const string sql = @"
SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM Routes WHERE RouteId = @Id)
    SELECT 1;
ELSE IF EXISTS (SELECT 1 FROM Routes WHERE Origin = @Origin AND Destination = @Destination AND RouteId <> @Id)
    SELECT 2;
ELSE
BEGIN
    UPDATE Routes SET Origin = @Origin, Destination = @Destination, DistanceKm = @Distance WHERE RouteId = @Id;
    SELECT 0;
END;";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = routeId;
            command.Parameters.Add("@Origin", SqlDbType.NVarChar, 100).Value = origin;
            command.Parameters.Add("@Destination", SqlDbType.NVarChar, 100).Value = destination;
            command.Parameters.Add("@Distance", SqlDbType.Decimal).Value = distanceKm is null ? DBNull.Value : distanceKm.Value;
            var code = (int)(await command.ExecuteScalarAsync())!;
            return code switch
            {
                0 => (true, null),
                1 => (false, T("Tuyến này không còn tồn tại.", "That route no longer exists.")),
                _ => (false, T("Đã có tuyến khác kết nối hai địa điểm đó.", "Another route already connects those two places."))
            };
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"UpdateRouteAsync failed for route {routeId}.", ex);
            return (false, T("Không thể cập nhật tuyến.", "The route could not be updated."));
        }
    }

    public static async Task<(bool Success, string? Error)> DeleteRouteAsync(int routeId)
    {
        RequireAdmin();
        const string sql = "DELETE FROM Routes WHERE RouteId = @Id AND NOT EXISTS (SELECT 1 FROM Trips WHERE RouteId = @Id);";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = routeId;
            return await command.ExecuteNonQueryAsync() == 1 ? (true, null) : (false, T("Tuyến này vẫn còn các chuyến xe đã lên lịch và không thể xóa.", "This route still has scheduled trips and cannot be deleted."));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"DeleteRouteAsync failed for route {routeId}.", ex);
            return (false, T("Không thể xóa tuyến.", "The route could not be deleted."));
        }
    }

    // ==================================================================
    // BUSES
    // ==================================================================

    public static async Task<IReadOnlyList<BusAdminItem>> GetBusesAsync()
    {
        RequireAdmin();
        const string sql = @"
SELECT b.BusId, b.LicensePlate, b.SeatCount, COALESCE(b.BusType, N'Standard'),
       COUNT(DISTINCT CASE WHEN t.Status IN (N'Đã đặt', N'Đã thanh toán', N'Đã sử dụng') THEN t.TicketId END),
       COALESCE(b.CompanyName, N'Sao Việt') AS CompanyName,
       COALESCE(b.DriverName, N'Nguyễn Văn Hùng') AS DriverName,
       COALESCE(b.DriverPhone, N'0912 345 678') AS DriverPhone,
       COALESCE(b.DepartureTimeNote, N'06:30, 09:00, 14:30') AS DepartureTimeNote,
       COALESCE(r.Origin + N' → ' + r.Destination, N'Chưa gán tuyến') AS RouteName,
       COALESCE(b.IntermediateStops, r.IntermediateStops, N'') AS IntermediateStops,
       COALESCE(b.Make, N'Hyundai') AS Make,
       COALESCE(b.Model, N'Universe') AS Model,
       COALESCE(b.ManufactureYear, 2023) AS ManufactureYear,
       COALESCE(b.Color, N'Trắng - Xanh') AS Color,
       COALESCE(b.RegistrationNumber, N'—') AS RegistrationNumber,
       COALESCE(b.InspectionNumber, N'—') AS InspectionNumber,
       b.RegistrationDate,
       b.InspectionExpiryDate,
       b.InsuranceExpiryDate,
       COALESCE(b.RegisteredEntity, N'—') AS RegisteredEntity,
       COALESCE(b.OperationType, N'Tuyến cố định') AS OperationType,
       (SELECT TOP 1 Price FROM Trips tr WHERE tr.BusId = b.BusId ORDER BY tr.TripId DESC) AS FullPrice,
       COALESCE(b.StagePrice, (SELECT TOP 1 StagePrice FROM Trips tr WHERE tr.BusId = b.BusId ORDER BY tr.TripId DESC)) AS StagePrice
FROM Buses b
LEFT JOIN Seats s ON s.BusId = b.BusId
LEFT JOIN TicketSeats ts ON ts.SeatId = s.SeatId
LEFT JOIN Tickets t ON t.TicketId = ts.TicketId
LEFT JOIN Routes r ON r.RouteId = b.RouteId
GROUP BY b.BusId, b.LicensePlate, b.SeatCount, b.BusType, b.CompanyName, b.DriverName, b.DriverPhone,
         b.DepartureTimeNote, r.Origin, r.Destination, b.IntermediateStops, r.IntermediateStops,
         b.Make, b.Model, b.ManufactureYear, b.Color, b.RegistrationNumber, b.InspectionNumber,
         b.RegistrationDate, b.InspectionExpiryDate, b.InsuranceExpiryDate, b.RegisteredEntity, b.OperationType,
         b.StagePrice
ORDER BY b.LicensePlate;";
        var list = new List<BusAdminItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new BusAdminItem(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetInt32(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                reader.IsDBNull(18) ? null : reader.GetDateTime(18),
                reader.IsDBNull(19) ? null : reader.GetDateTime(19),
                reader.GetString(20),
                reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetDecimal(22),
                reader.IsDBNull(23) ? null : reader.GetDecimal(23)));
        }
        return list;
    }

    public static async Task<IReadOnlyList<string>> GetCompaniesListAsync()
    {
        RequireAdmin();
        const string sql = @"
SELECT CompanyName FROM BusCompanies
UNION
SELECT DISTINCT CompanyName FROM Buses WHERE CompanyName IS NOT NULL AND CompanyName <> ''
ORDER BY CompanyName;";
        var list = new List<string>();
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Could not load company names.", ex);
        }
        if (list.Count == 0)
        {
            list.AddRange(new[] { "Sao Việt", "Hà Lan Buslines", "Hoàng Long", "Phương Trang (FUTA)", "Hải Vân Express" });
        }
        return list;
    }

    public static async Task<(bool Success, string? Error)> RegisterBusAsync(BusRegistrationInput input)
    {
        RequireAdmin();
        var plate = input.LicensePlate.Trim();
        var busType = string.IsNullOrWhiteSpace(input.BusType) ? $"{input.Make} {input.Model}".Trim() : input.BusType.Trim();
        if (string.IsNullOrWhiteSpace(plate)) return (false, T("Biển kiểm soát không được để trống.", "License plate is required."));
        if (input.SeatCount is < 1 or > 200) return (false, T("Số ghế phải từ 1 đến 200.", "Seat count must be between 1 and 200."));

        const string insertBus = @"
INSERT INTO Buses (
    LicensePlate, SeatCount, BusType, CompanyName, DriverName, DriverPhone,
    DepartureTimeNote, RouteId, IntermediateStops, Make, Model, ManufactureYear, Color,
    RegistrationNumber, InspectionNumber, RegistrationDate, InspectionExpiryDate, InsuranceExpiryDate,
    RegisteredEntity, OperationType, StagePrice
) OUTPUT INSERTED.BusId VALUES (
    @Plate, @Count, @Type, @CompanyName, @DriverName, @DriverPhone,
    @DepartureTimeNote, @RouteId, @IntermediateStops, @Make, @Model, @Year, @Color,
    @RegNumber, @InspNumber, @RegDate, @InspExpDate, @InsExpDate,
    @Entity, @OpType, @StagePrice
);";
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using var busCommand = new SqlCommand(insertBus, connection, transaction);
            busCommand.Parameters.Add("@Plate", SqlDbType.NVarChar, 20).Value = plate;
            busCommand.Parameters.Add("@Count", SqlDbType.Int).Value = input.SeatCount;
            busCommand.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(busType) ? "Standard" : busType;
            busCommand.Parameters.Add("@CompanyName", SqlDbType.NVarChar, 100).Value = (object?)input.CompanyName ?? DBNull.Value;
            busCommand.Parameters.Add("@DriverName", SqlDbType.NVarChar, 100).Value = (object?)input.DriverName ?? DBNull.Value;
            busCommand.Parameters.Add("@DriverPhone", SqlDbType.NVarChar, 20).Value = (object?)input.DriverPhone ?? DBNull.Value;
            busCommand.Parameters.Add("@DepartureTimeNote", SqlDbType.NVarChar, 100).Value = (object?)input.DepartureTimeNote ?? DBNull.Value;
            busCommand.Parameters.Add("@RouteId", SqlDbType.Int).Value = (object?)input.RouteId ?? DBNull.Value;
            busCommand.Parameters.Add("@IntermediateStops", SqlDbType.NVarChar, 500).Value = (object?)input.IntermediateStops ?? DBNull.Value;
            busCommand.Parameters.Add("@Make", SqlDbType.NVarChar, 50).Value = (object?)input.Make ?? DBNull.Value;
            busCommand.Parameters.Add("@Model", SqlDbType.NVarChar, 50).Value = (object?)input.Model ?? DBNull.Value;
            busCommand.Parameters.Add("@Year", SqlDbType.Int).Value = input.ManufactureYear > 1900 ? input.ManufactureYear : 2023;
            busCommand.Parameters.Add("@Color", SqlDbType.NVarChar, 50).Value = (object?)input.Color ?? DBNull.Value;
            busCommand.Parameters.Add("@RegNumber", SqlDbType.NVarChar, 50).Value = (object?)input.RegistrationNumber ?? DBNull.Value;
            busCommand.Parameters.Add("@InspNumber", SqlDbType.NVarChar, 50).Value = (object?)input.InspectionNumber ?? DBNull.Value;
            busCommand.Parameters.Add("@RegDate", SqlDbType.Date).Value = (object?)input.RegistrationDate ?? DBNull.Value;
            busCommand.Parameters.Add("@InspExpDate", SqlDbType.Date).Value = (object?)input.InspectionExpiryDate ?? DBNull.Value;
            busCommand.Parameters.Add("@InsExpDate", SqlDbType.Date).Value = (object?)input.InsuranceExpiryDate ?? DBNull.Value;
            busCommand.Parameters.Add("@Entity", SqlDbType.NVarChar, 150).Value = (object?)input.RegisteredEntity ?? DBNull.Value;
            busCommand.Parameters.Add("@OpType", SqlDbType.NVarChar, 50).Value = (object?)input.OperationType ?? DBNull.Value;
            busCommand.Parameters.Add("@StagePrice", SqlDbType.Decimal).Value = (object?)input.StagePrice ?? DBNull.Value;
            var busId = (int)(await busCommand.ExecuteScalarAsync())!;

            await using var seatCommand = new SqlCommand(SeatTopUpSql, connection, transaction);
            seatCommand.Parameters.Add("@BusId", SqlDbType.Int).Value = busId;
            seatCommand.Parameters.Add("@Count", SqlDbType.Int).Value = input.SeatCount;
            await seatCommand.ExecuteNonQueryAsync();

            if (!string.IsNullOrWhiteSpace(input.CompanyName))
            {
                await EnsureCompanyExistsInternalAsync(connection, transaction, input.CompanyName.Trim(), input.Hotline ?? input.DriverPhone, input.RouteId, input.OperatingArea, input.Description, input.Amenities);
            }
            if (input.RouteId.HasValue && input.RouteId.Value > 0)
            {
                await ScheduleTripsForBusInternalAsync(connection, transaction, busId, input.RouteId.Value, input.DepartureTimeNote, input.FarePrice, input.StagePrice);
            }

            await transaction.CommitAsync();
            return (true, null);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync();
            LoggerService.LogWarning($"RegisterBusAsync rejected duplicate license plate {plate}.");
            return (false, T("Biển kiểm soát này đã được đăng ký trong hệ thống.", "This license plate is already registered in the system."));
        }
        catch (SqlException ex)
        {
            await transaction.RollbackAsync();
            LoggerService.LogError($"RegisterBusAsync failed for plate {plate}.", ex);
            return (false, T("Không thể đăng ký xe vào bến. Vui lòng kiểm tra lại thông tin.", "Could not register the bus at the station. Please check the details."));
        }
    }

    public static Task<(bool Success, string? Error)> AddBusAsync(string plate, int seatCount, string busType)
    {
        return RegisterBusAsync(new BusRegistrationInput(
            CompanyName: "Sao Việt",
            DriverName: "Nguyễn Văn Hùng",
            DriverPhone: "0912 345 678",
            LicensePlate: plate,
            DepartureTimeNote: "06:30, 09:00, 14:30",
            RouteId: null,
            IntermediateStops: "",
            SeatCount: seatCount,
            BusType: busType,
            Make: "Hyundai",
            Model: "Universe",
            ManufactureYear: 2023,
            Color: "Trắng",
            RegistrationNumber: plate,
            InspectionNumber: "KD-001",
            RegistrationDate: DateTime.Today,
            InspectionExpiryDate: DateTime.Today.AddYears(2),
            InsuranceExpiryDate: DateTime.Today.AddYears(1),
            RegisteredEntity: "Công ty CP Vận tải Sao Việt",
            OperationType: "Tuyến cố định"));
    }

    public static async Task<(bool Success, string? Error)> UpdateBusDetailsAsync(int busId, BusRegistrationInput input)
    {
        RequireAdmin();
        var plate = input.LicensePlate.Trim();
        var busType = string.IsNullOrWhiteSpace(input.BusType) ? $"{input.Make} {input.Model}".Trim() : input.BusType.Trim();
        if (string.IsNullOrWhiteSpace(plate)) return (false, T("Biển kiểm soát không được để trống.", "License plate is required."));
        if (input.SeatCount is < 1 or > 200) return (false, T("Số ghế phải từ 1 đến 200.", "Seat count must be between 1 and 200."));

        const string preflight = @"
SELECT b.SeatCount,
       (SELECT COUNT(*) FROM Buses x WHERE x.LicensePlate = @Plate AND x.BusId <> @Id),
       (SELECT COUNT(*) FROM Seats s JOIN TicketSeats ts ON ts.SeatId = s.SeatId JOIN Tickets t ON t.TicketId = ts.TicketId
         WHERE s.BusId = @Id AND TRY_CONVERT(INT, s.SeatNumber) > @Count AND t.Status <> N'Đã hủy'),
       (SELECT COUNT(*) FROM Seats s JOIN TicketSeats ts ON ts.SeatId = s.SeatId JOIN Tickets t ON t.TicketId = ts.TicketId
         WHERE s.BusId = @Id AND TRY_CONVERT(INT, s.SeatNumber) > @Count)
FROM Buses b WHERE b.BusId = @Id;";
        const string updateBus = @"
UPDATE Buses SET
    LicensePlate = @Plate, SeatCount = @Count, BusType = @Type,
    CompanyName = @CompanyName, DriverName = @DriverName, DriverPhone = @DriverPhone,
    DepartureTimeNote = @DepartureTimeNote, RouteId = @RouteId, IntermediateStops = @IntermediateStops,
    Make = @Make, Model = @Model, ManufactureYear = @Year, Color = @Color,
    RegistrationNumber = @RegNumber, InspectionNumber = @InspNumber,
    RegistrationDate = @RegDate, InspectionExpiryDate = @InspExpDate, InsuranceExpiryDate = @InsExpDate,
    RegisteredEntity = @Entity, OperationType = @OpType, StagePrice = @StagePrice
WHERE BusId = @Id;";
        const string deleteSeats = "DELETE FROM Seats WHERE BusId = @Id AND TRY_CONVERT(INT, SeatNumber) > @Count;";

        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            int currentSeatCount;
            int duplicatePlates;
            int activeTicketsAboveCapacity;
            int anyTicketsAboveCapacity;

            await using (var check = new SqlCommand(preflight, connection, transaction))
            {
                check.Parameters.Add("@Id", SqlDbType.Int).Value = busId;
                check.Parameters.Add("@Plate", SqlDbType.NVarChar, 20).Value = plate;
                check.Parameters.Add("@Count", SqlDbType.Int).Value = input.SeatCount;
                await using var reader = await check.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await transaction.RollbackAsync();
                    return (false, T("Xe này không còn tồn tại trong hệ thống.", "This bus no longer exists in the system."));
                }
                currentSeatCount = reader.GetInt32(0);
                duplicatePlates = reader.GetInt32(1);
                activeTicketsAboveCapacity = reader.GetInt32(2);
                anyTicketsAboveCapacity = reader.GetInt32(3);
            }

            if (duplicatePlates > 0)
            {
                await transaction.RollbackAsync();
                return (false, T("Biển kiểm soát này đã được đăng ký cho xe khác.", "This license plate is already registered to another bus."));
            }
            if (input.SeatCount < currentSeatCount && activeTicketsAboveCapacity > 0)
            {
                await transaction.RollbackAsync();
                return (false, Format("Không thể giảm số ghế xuống {0}: có {1} vé đang hoạt động sử dụng ghế vượt quá số lượng này.", "Cannot reduce seat count to {0}: {1} active tickets use seats above this capacity.", input.SeatCount, activeTicketsAboveCapacity));
            }
            if (input.SeatCount < currentSeatCount && anyTicketsAboveCapacity > 0)
            {
                await transaction.RollbackAsync();
                return (false, Format("Không thể giảm số ghế xuống {0}: các ghế vượt quá chứa lịch sử vé cần được bảo tồn.", "Cannot reduce seat count to {0}: seats above this capacity have ticket history that must be preserved.", input.SeatCount));
            }

            await using (var update = new SqlCommand(updateBus, connection, transaction))
            {
                update.Parameters.Add("@Id", SqlDbType.Int).Value = busId;
                update.Parameters.Add("@Plate", SqlDbType.NVarChar, 20).Value = plate;
                update.Parameters.Add("@Count", SqlDbType.Int).Value = input.SeatCount;
                update.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(busType) ? "Standard" : busType;
                update.Parameters.Add("@CompanyName", SqlDbType.NVarChar, 100).Value = (object?)input.CompanyName ?? DBNull.Value;
                update.Parameters.Add("@DriverName", SqlDbType.NVarChar, 100).Value = (object?)input.DriverName ?? DBNull.Value;
                update.Parameters.Add("@DriverPhone", SqlDbType.NVarChar, 20).Value = (object?)input.DriverPhone ?? DBNull.Value;
                update.Parameters.Add("@DepartureTimeNote", SqlDbType.NVarChar, 100).Value = (object?)input.DepartureTimeNote ?? DBNull.Value;
                update.Parameters.Add("@RouteId", SqlDbType.Int).Value = (object?)input.RouteId ?? DBNull.Value;
                update.Parameters.Add("@IntermediateStops", SqlDbType.NVarChar, 500).Value = (object?)input.IntermediateStops ?? DBNull.Value;
                update.Parameters.Add("@Make", SqlDbType.NVarChar, 50).Value = (object?)input.Make ?? DBNull.Value;
                update.Parameters.Add("@Model", SqlDbType.NVarChar, 50).Value = (object?)input.Model ?? DBNull.Value;
                update.Parameters.Add("@Year", SqlDbType.Int).Value = input.ManufactureYear > 1900 ? input.ManufactureYear : 2023;
                update.Parameters.Add("@Color", SqlDbType.NVarChar, 50).Value = (object?)input.Color ?? DBNull.Value;
                update.Parameters.Add("@RegNumber", SqlDbType.NVarChar, 50).Value = (object?)input.RegistrationNumber ?? DBNull.Value;
                update.Parameters.Add("@InspNumber", SqlDbType.NVarChar, 50).Value = (object?)input.InspectionNumber ?? DBNull.Value;
                update.Parameters.Add("@RegDate", SqlDbType.Date).Value = (object?)input.RegistrationDate ?? DBNull.Value;
                update.Parameters.Add("@InspExpDate", SqlDbType.Date).Value = (object?)input.InspectionExpiryDate ?? DBNull.Value;
                update.Parameters.Add("@InsExpDate", SqlDbType.Date).Value = (object?)input.InsuranceExpiryDate ?? DBNull.Value;
                update.Parameters.Add("@Entity", SqlDbType.NVarChar, 150).Value = (object?)input.RegisteredEntity ?? DBNull.Value;
                update.Parameters.Add("@OpType", SqlDbType.NVarChar, 50).Value = (object?)input.OperationType ?? DBNull.Value;
                update.Parameters.Add("@StagePrice", SqlDbType.Decimal).Value = (object?)input.StagePrice ?? DBNull.Value;
                await update.ExecuteNonQueryAsync();
            }

            if (input.SeatCount < currentSeatCount)
            {
                await using var prune = new SqlCommand(deleteSeats, connection, transaction);
                prune.Parameters.Add("@Id", SqlDbType.Int).Value = busId;
                prune.Parameters.Add("@Count", SqlDbType.Int).Value = input.SeatCount;
                await prune.ExecuteNonQueryAsync();
            }
            else
            {
                await using var grow = new SqlCommand(SeatTopUpSql, connection, transaction);
                grow.Parameters.Add("@BusId", SqlDbType.Int).Value = busId;
                grow.Parameters.Add("@Count", SqlDbType.Int).Value = input.SeatCount;
                await grow.ExecuteNonQueryAsync();
            }

            if (!string.IsNullOrWhiteSpace(input.CompanyName))
            {
                await EnsureCompanyExistsInternalAsync(connection, transaction, input.CompanyName.Trim(), input.Hotline ?? input.DriverPhone, input.RouteId, input.OperatingArea, input.Description, input.Amenities);
            }
            if (input.RouteId.HasValue && input.RouteId.Value > 0)
            {
                await ScheduleTripsForBusInternalAsync(connection, transaction, busId, input.RouteId.Value, input.DepartureTimeNote, input.FarePrice, input.StagePrice);
            }

            await transaction.CommitAsync();
            return (true, null);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync();
            LoggerService.LogWarning($"UpdateBusDetailsAsync rejected duplicate license plate {plate}.");
            return (false, T("Biển kiểm soát này đã được đăng ký cho xe khác.", "This license plate is already registered to another bus."));
        }
        catch (SqlException ex)
        {
            await transaction.RollbackAsync();
            LoggerService.LogError($"UpdateBusDetailsAsync failed for plate {plate}.", ex);
            return (false, T("Không thể cập nhật thông tin xe.", "Could not update the bus details."));
        }
    }

    // Legacy overload delegates to RegisterBusAsync

    /// <summary>
    /// Creates every seat number from 1..@Count that the bus does not have yet.
    /// Used both when registering a bus and when growing its capacity.
    /// </summary>
    private const string SeatTopUpSql = @"
;WITH Numbers AS
(
    SELECT 1 AS Number
    UNION ALL SELECT Number + 1 FROM Numbers WHERE Number < @Count
)
INSERT INTO Seats (BusId, SeatNumber)
SELECT @BusId, CONVERT(NVARCHAR(10), n.Number)
FROM Numbers n
WHERE NOT EXISTS (SELECT 1 FROM Seats s WHERE s.BusId = @BusId AND s.SeatNumber = CONVERT(NVARCHAR(10), n.Number))
OPTION (MAXRECURSION 0);";

    private static async Task EnsureCompanyExistsInternalAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string companyName,
        string? hotline,
        int? routeId,
        string? operatingArea = null,
        string? description = null,
        string? amenities = null)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM BusCompanies WHERE CompanyName = @Name)
BEGIN
    DECLARE @Area NVARCHAR(200) = NULLIF(@AreaInput, N'');
    IF (@AreaInput IS NULL OR @AreaInput = '') AND @RouteId IS NOT NULL
    BEGIN
        SELECT @Area = Origin + N' - ' + Destination FROM Routes WHERE RouteId = @RouteId;
    END;
    INSERT INTO BusCompanies (CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor)
    VALUES (
        @Name,
        NULLIF(@Hotline, N''),
        0,
        0,
        COALESCE(@Area, N''),
        COALESCE(NULLIF(@Desc, N''), N''),
        COALESCE(NULLIF(@Amenities, N''), N''),
        @Logo,
        @Color
    );
END
ELSE
BEGIN
    UPDATE BusCompanies SET
        Hotline = COALESCE(NULLIF(@Hotline, N''), Hotline),
        OperatingArea = COALESCE(NULLIF(@AreaInput, N''), OperatingArea),
        Description = COALESCE(NULLIF(@Desc, N''), Description),
        Amenities = COALESCE(NULLIF(@Amenities, N''), Amenities)
    WHERE CompanyName = @Name;
END;";

        var logo = GenerateLogoText(companyName);
        var color = PickBrandColor(companyName);

        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Value = companyName;
        cmd.Parameters.Add("@Hotline", SqlDbType.NVarChar, 20).Value = (object?)hotline ?? DBNull.Value;
        cmd.Parameters.Add("@RouteId", SqlDbType.Int).Value = (object?)routeId ?? DBNull.Value;
        cmd.Parameters.Add("@AreaInput", SqlDbType.NVarChar, 200).Value = (object?)operatingArea ?? DBNull.Value;
        cmd.Parameters.Add("@Desc", SqlDbType.NVarChar, -1).Value = (object?)description ?? DBNull.Value;
        cmd.Parameters.Add("@Amenities", SqlDbType.NVarChar, 500).Value = (object?)amenities ?? DBNull.Value;
        cmd.Parameters.Add("@Logo", SqlDbType.NVarChar, 10).Value = logo;
        cmd.Parameters.Add("@Color", SqlDbType.NVarChar, 20).Value = color;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string GenerateLogoText(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "XE";
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
        return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
    }

    private static string PickBrandColor(string name)
    {
        string[] palette = { "#0F4C81", "#1E40AF", "#047857", "#B45309", "#7C3AED", "#BE123C", "#0369A1" };
        int hash = Math.Abs(name.GetHashCode());
        return palette[hash % palette.Length];
    }

    private static async Task ScheduleTripsForBusInternalAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int busId,
        int routeId,
        string? departureTimeNote,
        decimal? customPrice = null,
        decimal? stagePrice = null)
    {
        decimal distanceKm = 100m;
        const string routeSql = "SELECT COALESCE(DistanceKm, 100) FROM Routes WHERE RouteId = @RouteId;";
        await using (var rCmd = new SqlCommand(routeSql, connection, transaction))
        {
            rCmd.Parameters.Add("@RouteId", SqlDbType.Int).Value = routeId;
            var rVal = await rCmd.ExecuteScalarAsync();
            if (rVal is decimal d && d > 0) distanceKm = d;
        }
        int durationMinutes = (int)Math.Max(60, Math.Round((distanceKm / 50m) * 60m));

        decimal price = 140000m;
        if (customPrice.HasValue && customPrice.Value > 0)
        {
            price = customPrice.Value;
        }
        else
        {
            const string priceSql = "SELECT TOP 1 Price FROM Trips WHERE RouteId = @RouteId ORDER BY TripId DESC;";
            await using (var pCmd = new SqlCommand(priceSql, connection, transaction))
            {
                pCmd.Parameters.Add("@RouteId", SqlDbType.Int).Value = routeId;
                var pVal = await pCmd.ExecuteScalarAsync();
                if (pVal is decimal p && p > 0) price = p;
                else price = Math.Max(100000m, Math.Round((distanceKm * 1200m) / 10000m) * 10000m);
            }
        }
        var times = new List<TimeSpan>();
        if (!string.IsNullOrWhiteSpace(departureTimeNote))
        {
            var parts = departureTimeNote.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (TimeSpan.TryParse(part, out var ts))
                    times.Add(ts);
            }
        }
        if (times.Count == 0)
        {
            times.Add(new TimeSpan(7, 30, 0));
            times.Add(new TimeSpan(14, 0, 0));
        }

        const string insertTripSql = @"
IF NOT EXISTS (SELECT 1 FROM Trips WHERE BusId = @BusId AND RouteId = @RouteId AND DepartureTime = @DepartureTime)
BEGIN
    INSERT INTO Trips (RouteId, BusId, DepartureTime, ArrivalTime, Price, StagePrice)
    VALUES (@RouteId, @BusId, @DepartureTime, @ArrivalTime, @Price, @StagePrice);
END;";
        var now = DateTime.Now;
        var today = DateTime.Today;

        for (int day = 0; day <= 14; day++)
        {
            var targetDate = today.AddDays(day);
            foreach (var time in times)
            {
                var departureTime = targetDate.Add(time);
                if (departureTime <= now) continue;

                var arrivalTime = departureTime.AddMinutes(durationMinutes);
                await using var tCmd = new SqlCommand(insertTripSql, connection, transaction);
                tCmd.Parameters.Add("@RouteId", SqlDbType.Int).Value = routeId;
                tCmd.Parameters.Add("@BusId", SqlDbType.Int).Value = busId;
                tCmd.Parameters.Add("@DepartureTime", SqlDbType.DateTime2).Value = departureTime;
                tCmd.Parameters.Add("@ArrivalTime", SqlDbType.DateTime2).Value = arrivalTime;
                tCmd.Parameters.Add("@Price", SqlDbType.Decimal).Value = price;
                tCmd.Parameters.Add("@StagePrice", SqlDbType.Decimal).Value = (object?)stagePrice ?? DBNull.Value;
                await tCmd.ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>
    /// Full bus edit. Growing the capacity generates the missing seat rows; shrinking is
    /// refused while seats above the new capacity are still referenced by tickets.
    /// </summary>
    public static Task<(bool Success, string? Error)> UpdateBusAsync(int busId, string licensePlate, int seatCount, string busType)
    {
        return UpdateBusDetailsAsync(busId, new BusRegistrationInput(
            CompanyName: "Sao Việt",
            DriverName: "Nguyễn Văn Hùng",
            DriverPhone: "0912 345 678",
            LicensePlate: licensePlate,
            DepartureTimeNote: "06:30, 09:00, 14:30",
            RouteId: null,
            IntermediateStops: "",
            SeatCount: seatCount,
            BusType: busType,
            Make: "Hyundai",
            Model: "Universe",
            ManufactureYear: 2023,
            Color: "Trắng",
            RegistrationNumber: licensePlate,
            InspectionNumber: "KD-001",
            RegistrationDate: DateTime.Today,
            InspectionExpiryDate: DateTime.Today.AddYears(2),
            InsuranceExpiryDate: DateTime.Today.AddYears(1),
            RegisteredEntity: "Công ty CP Vận tải Sao Việt",
            OperationType: "Tuyến cố định"));
    }

    /// <summary>
    /// Removes a bus together with its generated seat map. Refused while the bus is assigned
    /// to a trip or referenced by any ticket.
    /// </summary>
    public static async Task<(bool Success, string? Error)> DeleteBusAsync(int busId)
    {
        RequireAdmin();
        const string sql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;  /* without this a failing DELETE leaves the batch running and
                       COMMIT would persist the seat wipe while reporting success */
IF NOT EXISTS (SELECT 1 FROM Buses WITH (UPDLOCK, HOLDLOCK) WHERE BusId = @Id)
    SELECT 1;
ELSE IF EXISTS (SELECT 1 FROM Trips WITH (UPDLOCK, HOLDLOCK) WHERE BusId = @Id)
    SELECT 2;
ELSE IF EXISTS (SELECT 1 FROM TicketSeats ts WITH (UPDLOCK, HOLDLOCK) JOIN Seats s ON s.SeatId = ts.SeatId WHERE s.BusId = @Id)
    SELECT 3;
ELSE
BEGIN
    BEGIN TRANSACTION;
    DELETE FROM Seats WHERE BusId = @Id;
    DELETE FROM Buses WHERE BusId = @Id;
    COMMIT TRANSACTION;
    SELECT 0;
END;";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = busId;
            var code = (int)(await command.ExecuteScalarAsync())!;
            return code switch
            {
                0 => (true, null),
                1 => (false, T("Xe này không còn tồn tại.", "That bus no longer exists.")),
                2 => (false, T("Xe này được phân công cho các chuyến xe đã lên lịch và không thể xóa.", "This bus is assigned to scheduled trips and cannot be deleted.")),
                _ => (false, T("Có vé liên kết với ghế của xe này nên không thể xóa.", "Tickets reference this bus's seats, so it cannot be deleted."))
            };
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"DeleteBusAsync failed for bus {busId}.", ex);
            return (false, T("Không thể xóa xe.", "The bus could not be deleted."));
        }
    }

    // ==================================================================
    // TRIPS
    // ==================================================================

    public static async Task<IReadOnlyList<TripAdminItem>> GetTripsAsync()
    {
        RequireAdmin();
        const string sql = @"
SELECT t.TripId, t.RouteId, t.BusId, r.Origin + N' → ' + r.Destination,
       b.LicensePlate, t.DepartureTime, t.ArrivalTime, t.Price, t.Status
FROM Trips t JOIN Routes r ON r.RouteId = t.RouteId JOIN Buses b ON b.BusId = t.BusId
ORDER BY t.DepartureTime DESC;";
        var list = new List<TripAdminItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(new TripAdminItem(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetString(3), reader.GetString(4),
            DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
            reader.GetDecimal(7),
            reader.GetString(8)));
        return list;
    }

    public static async Task<(bool Success, string? Error)> AddTripAsync(int routeId, int busId, DateTime departure, DateTime arrival, decimal price)
    {
        RequireAdmin();
        if (arrival <= departure) return (false, T("Giờ đến phải sau giờ khởi hành.", "Arrival must be later than departure."));
        if (price < 0) return (false, T("Giá vé không được âm.", "Fare cannot be negative."));

        var departureUtc = departure.Kind == DateTimeKind.Utc ? departure : departure.ToUniversalTime();
        var arrivalUtc = arrival.Kind == DateTimeKind.Utc ? arrival : arrival.ToUniversalTime();

        const string sql = @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
INSERT INTO Trips (RouteId, BusId, DepartureTime, ArrivalTime, Price, Status)
SELECT @Route, @Bus, @Departure, @Arrival, @Price, N'Scheduled'
WHERE EXISTS (SELECT 1 FROM Routes WHERE RouteId = @Route)
  AND EXISTS (SELECT 1 FROM Buses WHERE BusId = @Bus)
  /* HOLDLOCK keeps the range locked until commit, so two admins cannot both pass
     the overlap check and schedule one bus onto two simultaneous trips. */
  AND NOT EXISTS (SELECT 1 FROM Trips WITH (UPDLOCK, HOLDLOCK)
                  WHERE BusId = @Bus AND DepartureTime < @Arrival AND ArrivalTime > @Departure)"
  + RouteInStationScope + @";
COMMIT TRANSACTION;";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Route", SqlDbType.Int).Value = routeId;
            command.Parameters.Add("@Bus", SqlDbType.Int).Value = busId;
            command.Parameters.Add("@Departure", SqlDbType.DateTime2).Value = departureUtc;
            command.Parameters.Add("@Arrival", SqlDbType.DateTime2).Value = arrivalUtc;
            var amount = command.Parameters.Add("@Price", SqlDbType.Decimal);
            amount.Precision = 10;
            amount.Scale = 0;
            amount.Value = price;
            AddScopeParameter(command);
            if (await command.ExecuteNonQueryAsync() == 1) return (true, null);
            // Distinguish an out-of-scope route from a genuine scheduling clash so the
            // operator is told which rule refused the write.
            return await RouteIsInScopeAsync(routeId)
                ? (false, T("Xe này đã có chuyến trùng giờ, hoặc tuyến/xe không hợp lệ.", "That bus already runs an overlapping trip, or the route/bus is invalid."))
                : (false, OutOfScopeMessage);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // UX_Trips_Bus_Departure: another admin scheduled this exact slot first.
            return (false, T("Xe này đã có chuyến trùng giờ, hoặc tuyến/xe không hợp lệ.", "That bus already runs an overlapping trip, or the route/bus is invalid."));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"AddTripAsync failed for route {routeId} / bus {busId}.", ex);
            return (false, T("Không thể tạo chuyến xe.", "The trip could not be created."));
        }
    }

    /// <summary>
    /// Full trip edit including reassignment to another route or bus. A bus change is refused
    /// once tickets exist, because their seats belong to the previously assigned bus.
    /// </summary>
    public static async Task<(bool Success, string? Error)> UpdateTripAsync(int tripId, int routeId, int busId, DateTime departure, DateTime arrival, decimal price)
    {
        RequireAdmin();
        if (arrival <= departure) return (false, T("Giờ đến phải sau giờ khởi hành.", "Arrival must be later than departure."));
        if (price < 0) return (false, T("Giá vé không được âm.", "Fare cannot be negative."));

        var departureUtc = departure.Kind == DateTimeKind.Utc ? departure : departure.ToUniversalTime();
        var arrivalUtc = arrival.Kind == DateTimeKind.Utc ? arrival : arrival.ToUniversalTime();

        const string sql = @"
SET NOCOUNT ON;
DECLARE @CurrentBus INT = (SELECT BusId FROM Trips WHERE TripId = @TripId);
IF @CurrentBus IS NULL
    SELECT 1;
ELSE IF NOT EXISTS (SELECT 1 FROM Routes WHERE RouteId = @Route)
    SELECT 2;
ELSE IF NOT EXISTS (SELECT 1 FROM Buses WHERE BusId = @Bus)
    SELECT 3;
/* The target route and the trip's current route must both serve this station,
   so a trip cannot be moved into or out of the workstation's scope (AUD-D-005). */
ELSE IF NOT EXISTS (SELECT 1 FROM Routes sc WHERE sc.RouteId = @Route AND (sc.Origin LIKE @Scope OR sc.Destination LIKE @Scope))
     OR NOT EXISTS (SELECT 1 FROM Trips t JOIN Routes sc ON sc.RouteId = t.RouteId
                    WHERE t.TripId = @TripId AND (sc.Origin LIKE @Scope OR sc.Destination LIKE @Scope))
    SELECT 6;
ELSE IF @CurrentBus <> @Bus AND EXISTS (SELECT 1 FROM Tickets WHERE TripId = @TripId)
    SELECT 4;
ELSE IF EXISTS (SELECT 1 FROM Trips WITH (UPDLOCK, HOLDLOCK) WHERE BusId = @Bus AND TripId <> @TripId AND DepartureTime < @Arrival AND ArrivalTime > @Departure)
    SELECT 5;
ELSE
BEGIN
    UPDATE Trips
    SET RouteId = @Route, BusId = @Bus, DepartureTime = @Departure, ArrivalTime = @Arrival, Price = @Price
    WHERE TripId = @TripId;
    SELECT 0;
END;";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@TripId", SqlDbType.Int).Value = tripId;
            command.Parameters.Add("@Route", SqlDbType.Int).Value = routeId;
            command.Parameters.Add("@Bus", SqlDbType.Int).Value = busId;
            command.Parameters.Add("@Departure", SqlDbType.DateTime2).Value = departureUtc;
            command.Parameters.Add("@Arrival", SqlDbType.DateTime2).Value = arrivalUtc;
            var pPrice = command.Parameters.Add("@Price", SqlDbType.Decimal);
            pPrice.Precision = 10;
            pPrice.Scale = 0;
            pPrice.Value = price;
            AddScopeParameter(command);
            var code = (int)(await command.ExecuteScalarAsync())!;
            return code switch
            {
                0 => (true, null),
                1 => (false, T("Chuyến xe này không còn tồn tại.", "That trip no longer exists.")),
                2 => (false, T("Tuyến đã chọn không còn tồn tại.", "The selected route no longer exists.")),
                3 => (false, T("Xe đã chọn không còn tồn tại.", "The selected bus no longer exists.")),
                4 => (false, T("Chuyến xe này đã có vé được phát hành nên không thể chuyển sang xe khác.", "Tickets have already been issued for this trip, so it cannot be moved to another bus.")),
                6 => (false, OutOfScopeMessage),
                _ => (false, T("Xe đã chọn đã có chuyến trùng giờ trong khoảng thời gian đó.", "The selected bus already runs an overlapping trip in that time window."))
            };
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"UpdateTripAsync failed for trip {tripId}.", ex);
            return (false, T("Không thể cập nhật chuyến xe.", "The trip could not be updated."));
        }
    }

    public static async Task<(bool Success, string? Error)> DeleteTripAsync(int tripId)
    {
        RequireAdmin();
        const string sql = @"
DELETE FROM Trips
WHERE TripId = @Id
  AND NOT EXISTS (SELECT 1 FROM Tickets WHERE TripId = @Id)
  AND EXISTS (SELECT 1 FROM Routes sc JOIN Trips t ON t.RouteId = sc.RouteId
              WHERE t.TripId = @Id AND (sc.Origin LIKE @Scope OR sc.Destination LIKE @Scope));";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = tripId;
            AddScopeParameter(command);
            return await command.ExecuteNonQueryAsync() == 1
                ? (true, null)
                : (false, T("Chuyến xe này đã có vé hoặc nằm ngoài phạm vi bến của máy trạm nên không thể xóa.", "Tickets exist for this trip, or it is outside this workstation's station scope, so it cannot be deleted."));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"DeleteTripAsync failed for trip {tripId}.", ex);
            return (false, T("Không thể xóa chuyến xe.", "The trip could not be deleted."));
        }
    }

    /// <summary>
    /// Cancels a scheduled departure. Cancels all active tickets on the trip, marks
    /// settled payments for refund review, fails pending holds, and writes an audit log.
    /// </summary>
    public static async Task<(bool Success, string? Error)> CancelTripAsync(int tripId, string? reason = null)
    {
        RequireAdmin();
        const string sql = @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @CurrentStatus NVARCHAR(20), @Departure DATETIME2;
SELECT @CurrentStatus = Status, @Departure = DepartureTime
FROM dbo.Trips WITH (UPDLOCK, HOLDLOCK)
WHERE TripId = @Id;

IF @CurrentStatus IS NULL
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 1; -- Not found
END
ELSE IF @CurrentStatus = N'Cancelled'
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 2; -- Already cancelled
END
ELSE IF @CurrentStatus = N'Departed' OR @Departure <= SYSUTCDATETIME()
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 3; -- Cannot cancel a departed trip
END
ELSE
BEGIN
    -- 1. Mark trip as cancelled
    UPDATE dbo.Trips SET Status = N'Cancelled' WHERE TripId = @Id;

    -- 2. Cancel all active tickets on this trip
    UPDATE dbo.Tickets
    SET Status = N'Đã hủy', CancelledAt = SYSUTCDATETIME(), PaymentExpiresAt = NULL
    WHERE TripId = @Id AND Status IN (N'Đã đặt', N'Đã thanh toán');

    -- 3. Mark settled payments for refund review
    UPDATE p
    SET p.RefundRequestedAt = COALESCE(p.RefundRequestedAt, SYSUTCDATETIME()),
        p.Notes = COALESCE(p.Notes, N'Chuyến xe bị hủy bởi ban quản trị.')
    FROM dbo.Payments p
    JOIN dbo.Tickets tk ON tk.TicketId = p.TicketId
    WHERE tk.TripId = @Id AND p.Status = N'Success';

    -- 4. Fail pending holds
    UPDATE p
    SET p.Status = N'Failed',
        p.Notes = COALESCE(p.Notes, N'Chuyến xe bị hủy bởi ban quản trị.')
    FROM dbo.Payments p
    JOIN dbo.Tickets tk ON tk.TicketId = p.TicketId
    WHERE tk.TripId = @Id AND p.Status = N'Pending';

    COMMIT TRANSACTION;
    SELECT 0; -- Success
END;";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = tripId;
            var code = (int)(await command.ExecuteScalarAsync())!;
            if (code == 0)
            {
                await AuditLog.WriteAsync(connection, null, "TripCancelled", "Trip", tripId.ToString(), reason);
                LoggerService.LogInfo($"Admin {CurrentUser.Account?.AccountId} cancelled trip {tripId}. Reason: {reason ?? "(none)"}");
            }
            return code switch
            {
                0 => (true, null),
                1 => (false, T("Chuyến xe này không còn tồn tại.", "That trip no longer exists.")),
                2 => (false, T("Chuyến xe này đã bị hủy.", "This trip has already been cancelled.")),
                _ => (false, T("Chuyến xe này đã khởi hành và không thể hủy.", "This trip has already departed and cannot be cancelled."))
            };
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"CancelTripAsync failed for trip {tripId}.", ex);
            return (false, T("Không thể hủy chuyến xe.", "The trip could not be cancelled."));
        }
    }

    // ==================================================================
    // ACCOUNTS
    // ==================================================================

    public static async Task<IReadOnlyList<AccountAdminItem>> GetAccountsAsync()
    {
        RequireAdmin();
        const string sql = "SELECT AccountId, Username, FullName, Email, Phone, Role, CreatedAt FROM Accounts ORDER BY CreatedAt DESC;";
        var list = new List<AccountAdminItem>();
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new AccountAdminItem(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetDateTime(6)));
        }
        return list;
    }

    public static async Task<string?> GetAccountRoleAsync(int accountId)
    {
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        const string sql = "SELECT Role FROM Accounts WHERE AccountId = @Id;";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = accountId;
        var result = await cmd.ExecuteScalarAsync();
        return result is string role ? role : null;
    }

    public static async Task WriteRoleAuditLogAsync(int? actorId, int targetId, string action, string? oldValue, string? newValue)
    {
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            const string sql = @"
INSERT INTO dbo.AuditLogs (ActorId, TargetId, Action, OldValue, NewValue, CreatedAt)
VALUES (@ActorId, @TargetId, @Action, @OldValue, @NewValue, SYSUTCDATETIME());";
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.Add("@ActorId", SqlDbType.Int).Value = (object?)actorId ?? DBNull.Value;
            cmd.Parameters.Add("@TargetId", SqlDbType.Int).Value = targetId;
            cmd.Parameters.Add("@Action", SqlDbType.NVarChar, 50).Value = action;
            cmd.Parameters.Add("@OldValue", SqlDbType.NVarChar, 50).Value = (object?)oldValue ?? DBNull.Value;
            cmd.Parameters.Add("@NewValue", SqlDbType.NVarChar, 50).Value = (object?)newValue ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Could not write role audit log.", ex);
        }
    }

    public static async Task<IReadOnlyList<RoleAuditLogItem>> GetRecentRoleAuditLogsAsync(int top = 10)
    {
        RequireAdmin();
        if (!CurrentUser.IsOwner) return [];
        const string sql = @"
SELECT TOP (@Top)
    l.Id, l.ActorId, a.Username AS ActorUsername,
    l.TargetId, t.Username AS TargetUsername,
    l.Action, l.OldValue, l.NewValue, l.CreatedAt
FROM dbo.AuditLogs l
LEFT JOIN dbo.Accounts a ON l.ActorId = a.AccountId
LEFT JOIN dbo.Accounts t ON l.TargetId = t.AccountId
ORDER BY l.CreatedAt DESC;";
        var list = new List<RoleAuditLogItem>();
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.Add("@Top", SqlDbType.Int).Value = top;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new RoleAuditLogItem(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? $"#{reader.GetInt32(3)}" : reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetDateTime(8)));
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Could not fetch role audit logs.", ex);
        }
        return list;
    }

    public static async Task<(bool Success, string? Error)> UpdateAccountRoleAsync(int accountId, string newRole, string? ownerPassword = null)
    {
        RequireAdmin();
        var actor = CurrentUser.Account;
        var actorId = actor?.AccountId ?? 0;

        if (newRole is not ("Customer" or "Admin" or "Owner"))
            return (false, T("Vai trò không hợp lệ.", "Unknown role."));

        // Rule 4b: Nobody can change their own role (actorId == targetId -> reject)
        if (accountId == actorId)
        {
            await WriteRoleAuditLogAsync(actorId, accountId, "RoleChangeRejected_SelfChange", actor?.Role, newRole);
            return (false, T("Bạn không thể tự thay đổi vai trò của chính mình.", "You cannot change your own role."));
        }

        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();

        const string getTargetSql = "SELECT Role, Username FROM Accounts WHERE AccountId = @Id;";
        await using var getTargetCmd = new SqlCommand(getTargetSql, connection);
        getTargetCmd.Parameters.Add("@Id", SqlDbType.Int).Value = accountId;
        string currentRole;
        string targetUsername;
        await using (var reader = await getTargetCmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return (false, T("Tài khoản này không còn tồn tại.", "That account no longer exists."));
            currentRole = reader.GetString(0);
            targetUsername = reader.GetString(1);
        }

        // Rule 4a: Only Owner can add or remove the Administrator role
        bool isPrivilegedRoleChange = newRole is "Admin" or "Owner" || currentRole is "Admin" or "Owner";
        if (isPrivilegedRoleChange && !CurrentUser.IsOwner)
        {
            await WriteRoleAuditLogAsync(actorId, accountId, "RoleChangeRejected_NotOwner", currentRole, newRole);
            return (false, T("Chỉ Chủ sở hữu (Owner) mới có quyền cấp hoặc gỡ quyền Quản trị viên.", "Only the Owner can grant or revoke the Administrator role."));
        }

        // Rule 4d: Never allow deleting or demoting the last remaining Owner
        if (currentRole == "Owner" && newRole != "Owner")
        {
            const string ownerCountSql = "SELECT COUNT(*) FROM Accounts WHERE Role = N'Owner';";
            await using var countCmd = new SqlCommand(ownerCountSql, connection);
            var ownerCount = (int)(await countCmd.ExecuteScalarAsync())!;
            if (ownerCount <= 1)
            {
                await WriteRoleAuditLogAsync(actorId, accountId, "RoleChangeRejected_LastOwner", currentRole, newRole);
                return (false, T("Không thể hạ quyền Owner duy nhất còn lại.", "Cannot demote the last remaining Owner."));
            }
        }

        // Rule 5: Re-authentication when Owner grants or revokes Administrator
        if (newRole is "Admin" or "Customer" && (newRole == "Admin" || currentRole == "Admin"))
        {
            if (string.IsNullOrWhiteSpace(ownerPassword))
            {
                await WriteRoleAuditLogAsync(actorId, accountId, "RoleChangeRejected_MissingPassword", currentRole, newRole);
                return (false, T("Vui lòng nhập mật khẩu xác nhận của bạn để thực hiện thay đổi quyền này.", "Please enter your confirmation password to perform this role change."));
            }

            const string getOwnerHashSql = "SELECT PasswordHash FROM Accounts WHERE AccountId = @ActorId;";
            await using var hashCmd = new SqlCommand(getOwnerHashSql, connection);
            hashCmd.Parameters.Add("@ActorId", SqlDbType.Int).Value = actorId;
            var ownerHash = (string?)await hashCmd.ExecuteScalarAsync();
            if (string.IsNullOrEmpty(ownerHash) || !DatabaseHelper.VerifyPassword(ownerPassword, ownerHash))
            {
                await WriteRoleAuditLogAsync(actorId, accountId, "RoleChangeRejected_WrongPassword", currentRole, newRole);
                return (false, T("Mật khẩu xác nhận không chính xác.", "Incorrect confirmation password."));
            }
        }

        const string sql = "UPDATE Accounts SET Role = @Role WHERE AccountId = @Id;";
        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = accountId;
            command.Parameters.Add("@Role", SqlDbType.NVarChar, 20).Value = newRole;
            if (await command.ExecuteNonQueryAsync() != 1) return (false, T("Tài khoản này không còn tồn tại.", "That account no longer exists."));

            await WriteRoleAuditLogAsync(actorId, accountId, "RoleChanged", currentRole, newRole);
            await AuditLog.WriteAsync(connection, null, "AccountRoleChanged", "Account", accountId.ToString(), $"Role changed from {currentRole} to {newRole}.");
            LoggerService.LogInfo($"Actor {actorId} set the role of account {accountId} ({targetUsername}) from {currentRole} to {newRole}.");
            return (true, null);
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"UpdateAccountRoleAsync failed for account {accountId}.", ex);
            return (false, T("Không thể cập nhật vai trò tài khoản.", "The account role could not be updated."));
        }
    }

    /// <summary>Administrative password reset. The new secret is stored as a PBKDF2 hash.</summary>
    public static async Task<(bool Success, string? Error)> ResetAccountPasswordAsync(int accountId, string newPassword)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(newPassword))
            return (false, T("Mật khẩu mới phải có từ 8 đến 128 ký tự và bao gồm chữ hoa, chữ thường và chữ số.", "The new password must be 8-128 characters and include an uppercase letter, a lowercase letter and a digit."));
        if (!DatabaseHelper.ValidatePassword(newPassword, out var policyError))
            return (false, policyError);

        var targetRole = await GetAccountRoleAsync(accountId);
        if (targetRole is null) return (false, T("Tài khoản này không còn tồn tại.", "That account no longer exists."));

        // Rule 4c: Only Owner can Reset password on an account that is Administrator or Owner
        if (targetRole is "Admin" or "Owner" && !CurrentUser.IsOwner)
        {
            return (false, T("Chỉ Chủ sở hữu (Owner) mới có quyền đặt lại mật khẩu cho Quản trị viên.", "Only the Owner can reset the password of an Administrator."));
        }

        const string sql = "UPDATE Accounts SET PasswordHash = @Hash WHERE AccountId = @Id;";
        try
        {
            var hash = DatabaseHelper.HashPassword(newPassword);
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = accountId;
            command.Parameters.Add("@Hash", SqlDbType.NVarChar, 255).Value = hash;
            if (await command.ExecuteNonQueryAsync() != 1) return (false, T("Tài khoản này không còn tồn tại.", "That account no longer exists."));
            await AuditLog.WriteAsync(connection, null, "AccountPasswordReset", "Account", accountId.ToString());
            LoggerService.LogInfo($"Admin {CurrentUser.Account?.AccountId} reset the password of account {accountId}.");
            return (true, null);
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"ResetAccountPasswordAsync failed for account {accountId}.", ex);
            return (false, T("Không thể đặt lại mật khẩu.", "The password could not be reset."));
        }
    }

    /// <summary>
    /// Removes an account. Refused for the signed-in administrator, for accounts that still
    /// hold live tickets, and for accounts referenced by ticket or settlement history.
    /// </summary>
    public static async Task<(bool Success, string? Error)> DeleteAccountAsync(int accountId)
    {
        RequireAdmin();
        if (accountId == CurrentUser.Account?.AccountId)
            return (false, T("Bạn không thể xóa tài khoản đang dùng để đăng nhập.", "You cannot delete the account you are signed in with."));

        var targetRole = await GetAccountRoleAsync(accountId);
        if (targetRole is null) return (false, T("Tài khoản này không còn tồn tại.", "That account no longer exists."));

        // Rule 4c: Only Owner can Delete an account that is Administrator or Owner
        if (targetRole is "Admin" or "Owner" && !CurrentUser.IsOwner)
        {
            return (false, T("Chỉ Chủ sở hữu (Owner) mới có quyền xóa tài khoản Quản trị viên.", "Only the Owner can delete an Administrator account."));
        }

        // Rule 4d: Never allow deleting the last remaining Owner
        if (targetRole == "Owner")
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            const string ownerCountSql = "SELECT COUNT(*) FROM Accounts WHERE Role = N'Owner';";
            await using var countCmd = new SqlCommand(ownerCountSql, connection);
            var ownerCount = (int)(await countCmd.ExecuteScalarAsync())!;
            if (ownerCount <= 1)
            {
                return (false, T("Không thể xóa Owner duy nhất còn lại.", "Cannot delete the last remaining Owner."));
            }
        }

        const string sql = @"
SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE AccountId = @Id)
    SELECT 1;
ELSE IF EXISTS (SELECT 1 FROM Tickets WHERE AccountId = @Id AND Status <> N'Đã hủy')
    SELECT 2;
ELSE IF EXISTS (SELECT 1 FROM Tickets WHERE AccountId = @Id)
    SELECT 3;
ELSE IF EXISTS (SELECT 1 FROM Payments WHERE ConfirmedByAccountId = @Id OR RefundApprovedByAccountId = @Id)
    SELECT 4;
ELSE
BEGIN
    DELETE FROM Accounts WHERE AccountId = @Id;
    SELECT 0;
END;";
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = accountId;
            var code = (int)(await command.ExecuteScalarAsync())!;
            if (code == 0)
            {
                await AuditLog.WriteAsync(connection, null, "AccountDeleted", "Account", accountId.ToString());
                LoggerService.LogInfo($"Admin {CurrentUser.Account?.AccountId} deleted account {accountId}.");
            }
            return code switch
            {
                0 => (true, null),
                1 => (false, T("Tài khoản này không còn tồn tại.", "That account no longer exists.")),
                2 => (false, T("Tài khoản này có vé đang hoạt động. Hãy hủy các vé trước khi xóa tài khoản.", "This account holds active tickets. Cancel them before deleting the account.")),
                3 => (false, T("Tài khoản này có lịch sử vé cần được bảo tồn. Hãy hạ quyền thay vì xóa.", "This account has ticket history that must be preserved. Demote it instead of deleting.")),
                _ => (false, T("Tài khoản này đã phê duyệt thanh toán hoặc hoàn tiền và phải được giữ lại để kiểm toán.", "This account approved settlements or refunds and must be kept for audit purposes."))
            };
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"DeleteAccountAsync failed for account {accountId}.", ex);
            return (false, T("Không thể xóa tài khoản.", "The account could not be deleted."));
        }
    }

    private static string? NormalizeLedgerStatus(string? statusFilter) => statusFilter?.Trim() switch
    {
        "Pending" or "Success" or "Failed" or "Refunded" => statusFilter!.Trim(),
        _ => null
    };

    private static string EscapeLikePattern(string term) => term
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);
}
