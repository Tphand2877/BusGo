using System.Data;
using System.Globalization;
using BusGo.Data;
using Microsoft.Data.SqlClient;
using BusGo.Models;
using static BusGo.Services.UiText;

namespace BusGo.Services;

/// <summary>
/// Moves paid tickets into their terminal <c>N'Đã sử dụng'</c> state, either automatically once
/// the coach has left or explicitly when an operator checks a passenger in at the door.
/// </summary>
public static class TicketLifecycleService
{
    /// <summary>Marks every paid ticket whose trip has already departed as used. Returns the number of tickets updated.</summary>
    public static async Task<int> MarkDepartedTicketsUsedAsync()
    {
        const string sql = @"
UPDATE t SET t.Status=N'Đã sử dụng', t.UsedAt=SYSUTCDATETIME()
FROM Tickets t WITH (UPDLOCK,HOLDLOCK)
JOIN Trips tr ON tr.TripId=t.TripId
WHERE t.Status=N'Đã thanh toán' AND tr.DepartureTime<=SYSUTCDATETIME();";
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Boarding check-in performed by an operator: a paid ticket becomes used.</summary>
    public static async Task<(bool Success, string? Error)> CheckInTicketAsync(int ticketId, int adminAccountId)
    {
        if (PaymentService.ValidateAdminSession(adminAccountId) is { } authError) return (false, authError);
        if (ticketId <= 0) return (false, T("Chưa chọn vé.", "No ticket was selected."));

        const string sql = @"
DECLARE @Result int = 0;
DECLARE @State nvarchar(50);
SELECT @State=t.Status FROM Tickets t WITH (UPDLOCK,HOLDLOCK) WHERE t.TicketId=@TicketId;
IF @State IS NULL SET @Result=1;
ELSE IF @State=N'Đã sử dụng' SET @Result=2;
ELSE IF @State=N'Đã hủy' SET @Result=3;
ELSE IF @State<>N'Đã thanh toán' SET @Result=4;
ELSE UPDATE Tickets SET Status=N'Đã sử dụng', UsedAt=SYSUTCDATETIME(), CheckedInByAccountId=@AdminAccountId WHERE TicketId=@TicketId;
SELECT @Result;";

        SqlTransaction? transaction = null;
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;
            command.Parameters.Add("@AdminAccountId", SqlDbType.Int).Value = adminAccountId;
            var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (outcome != 0)
            {
                await transaction.RollbackAsync();
                return (false, outcome switch
                {
                    1 => T("Vé này không còn tồn tại.", "That ticket no longer exists."),
                    2 => T("Vé này đã được soát lên xe.", "This ticket has already been checked in."),
                    3 => T("Vé này đã hủy và không thể soát lên xe.", "This ticket was cancelled and cannot be checked in."),
                    _ => T("Vé này chưa thanh toán nên không thể soát lên xe.", "This ticket is not paid yet, so it cannot be checked in.")
                });
            }
            await transaction.CommitAsync();
            LoggerService.LogInfo($"Ticket {ticketId} checked in by operator {adminAccountId}.");
            return (true, null);
        }
        catch (SqlException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Database failure while checking in ticket {ticketId}.", ex);
            return (false, T("Dữ liệu vé không khả dụng. Vui lòng thử lại.", "The ticket store is unavailable. Please try again."));
        }
        catch (InvalidOperationException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Invalid state while checking in ticket {ticketId}.", ex);
            return (false, T("Không thể hoàn tất soát vé.", "The check-in could not be completed."));
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public static string NormalizeTicketCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();

        // 1. JSON payload support
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var codeProp))
                    return codeProp.GetString()?.Trim().ToUpperInvariant() ?? string.Empty;
                if (root.TryGetProperty("ticketCode", out var tcProp))
                    return tcProp.GetString()?.Trim().ToUpperInvariant() ?? string.Empty;
            }
            catch {}
        }

        // 2. URL payload support
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var queryCode = query["code"] ?? query["id"] ?? query["ticketCode"];
            if (!string.IsNullOrWhiteSpace(queryCode))
                return queryCode.Trim().ToUpperInvariant();

            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrEmpty(lastSegment))
                trimmed = lastSegment;
        }

        // 3. Regex match for standard BusGo ticket pattern (e.g., TK-A1B2C3D4)
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"(TK-[A-Z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.ToUpperInvariant();

        return trimmed.ToUpperInvariant();
    }

    public static async Task<CheckInResult> ScanAndCheckInTicketAsync(string rawCode, int adminAccountId, string? currentStation = null)
    {
        if (PaymentService.ValidateAdminSession(adminAccountId) is { } authError)
            return new CheckInResult(CheckInOutcome.Invalid, false, authError, ErrorReason: authError);

        var code = NormalizeTicketCode(rawCode);
        if (string.IsNullOrWhiteSpace(code))
            return new CheckInResult(CheckInOutcome.Invalid, false, T("Mã vé không hợp lệ.", "Invalid ticket code."), ErrorReason: T("Mã vé trống hoặc định dạng không đúng.", "Empty code or invalid format."));

        const string querySql = @"
SELECT tk.TicketId, tk.TicketCode, tk.Status, tk.PaymentStatus, tk.UsedAt,
       tk.PassengerName, tk.PassengerPhone, tk.SeatCount,
       tr.DepartureTime, tr.ArrivalTime,
       r.Origin, r.Destination,
       tk.CheckedInByAccountId,
       (SELECT STRING_AGG(s.SeatNumber, N', ') WITHIN GROUP (ORDER BY s.SeatNumber)
        FROM TicketSeats ts JOIN Seats s ON s.SeatId = ts.SeatId WHERE ts.TicketId = tk.TicketId) AS SeatNumbers
FROM Tickets tk
JOIN Trips tr ON tr.TripId = tk.TripId
JOIN Routes r ON r.RouteId = tr.RouteId
WHERE tk.TicketCode = @Code;";

        int ticketId;
        string ticketCode, status, paymentStatus, passengerName, origin, destination;
        string? seatNumbers;
        DateTime? usedAt;
        DateTime departureTime;

        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using (var queryCmd = new SqlCommand(querySql, connection))
        {
            queryCmd.Parameters.Add("@Code", SqlDbType.NVarChar, 50).Value = code;
            await using var reader = await queryCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new CheckInResult(
                    CheckInOutcome.Invalid,
                    false,
                    T("Không tìm thấy vé trong hệ thống.", "Ticket not found in the system."),
                    TicketCode: code,
                    ErrorReason: T("Mã vé không tồn tại hoặc đã bị xóa.", "Ticket does not exist or was deleted."));
            }

            ticketId = reader.GetInt32(0);
            ticketCode = reader.GetString(1);
            status = reader.GetString(2);
            paymentStatus = reader.GetString(3);
            usedAt = reader.IsDBNull(4) ? null : DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
            passengerName = reader.GetString(5);
            departureTime = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
            origin = reader.GetString(10);
            destination = reader.GetString(11);
            seatNumbers = reader.IsDBNull(13) ? null : reader.GetString(13);
        }

        var routeSummary = $"{origin} → {destination}";
        var departureDisplay = departureTime.ToLocalTime().ToString("HH:mm dd/MM/yyyy");
        var seatsDisplay = string.IsNullOrWhiteSpace(seatNumbers) ? T("Chưa gán", "Unassigned") : seatNumbers;

        // 1. Idempotency check: Already checked in
        if (status == "Đã sử dụng" || usedAt.HasValue)
        {
            var formattedTime = usedAt.HasValue ? usedAt.Value.ToLocalTime().ToString("HH:mm dd/MM/yyyy") : T("(chưa rõ giờ)", "(unknown time)");
            return new CheckInResult(
                CheckInOutcome.AlreadyUsed,
                false,
                Format("Vé đã được soát lúc {0}.", "Ticket was already checked in at {0}.", formattedTime),
                TicketCode: ticketCode,
                PassengerName: passengerName,
                SeatNumbers: seatsDisplay,
                Route: routeSummary,
                DepartureTime: departureDisplay,
                CheckedInAt: formattedTime,
                ErrorReason: T("Vé đã được sử dụng trước đó, không thể soát lại.", "Ticket was already used, cannot check in again."));
        }

        // 2. Cancelled ticket check
        if (status == "Đã hủy")
        {
            return new CheckInResult(
                CheckInOutcome.Invalid,
                false,
                T("Vé đã bị hủy và không thể soát lên xe.", "This ticket was cancelled and cannot be checked in."),
                TicketCode: ticketCode,
                PassengerName: passengerName,
                SeatNumbers: seatsDisplay,
                Route: routeSummary,
                DepartureTime: departureDisplay,
                ErrorReason: T("Trạng thái vé là Đã hủy.", "Ticket status is Cancelled."));
        }

        // 3. Payment status check
        if (status != "Đã thanh toán" || paymentStatus != "Đã thanh toán")
        {
            return new CheckInResult(
                CheckInOutcome.Invalid,
                false,
                T("Vé chưa được thanh toán nên không thể soát lên xe.", "Ticket is unpaid and cannot be checked in."),
                TicketCode: ticketCode,
                PassengerName: passengerName,
                SeatNumbers: seatsDisplay,
                Route: routeSummary,
                DepartureTime: departureDisplay,
                ErrorReason: T("Vé chưa hoàn tất thanh toán tiền vé.", "Ticket payment has not been completed."));
        }


        // 6. Perform atomic check-in
        const string updateSql = @"
UPDATE dbo.Tickets
SET Status = N'Đã sử dụng',
    UsedAt = SYSUTCDATETIME(),
    CheckedInByAccountId = @AdminAccountId
WHERE TicketId = @TicketId AND Status = N'Đã thanh toán';";

        SqlTransaction? transaction = null;
        try
        {
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await using var updateCmd = new SqlCommand(updateSql, connection, transaction);
            updateCmd.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;
            updateCmd.Parameters.Add("@AdminAccountId", SqlDbType.Int).Value = adminAccountId;

            var rowsAffected = await updateCmd.ExecuteNonQueryAsync();
            if (rowsAffected != 1)
            {
                await transaction.RollbackAsync();
                return new CheckInResult(
                    CheckInOutcome.AlreadyUsed,
                    false,
                    T("Vé đã được soát bởi người khác vừa xong.", "Ticket was just checked in by another operator."),
                    TicketCode: ticketCode,
                    PassengerName: passengerName,
                    SeatNumbers: seatsDisplay,
                    Route: routeSummary,
                    DepartureTime: departureDisplay,
                    ErrorReason: T("Xung đột soát vé đồng thời.", "Concurrent check-in conflict."));
            }

            await AuditLog.WriteAsync(connection, transaction, "TicketCheckIn", "Ticket", ticketId.ToString(CultureInfo.InvariantCulture), $"Checked in via scanner by admin {adminAccountId} for route {routeSummary}.");
            await transaction.CommitAsync();

            var checkInNowDisplay = DateTime.UtcNow.ToLocalTime().ToString("HH:mm dd/MM/yyyy");
            LoggerService.LogInfo($"Ticket {ticketId} ({ticketCode}) checked in successfully by operator {adminAccountId}.");

            return new CheckInResult(
                CheckInOutcome.Success,
                true,
                T("Hợp lệ - đã soát vé", "Valid - checked in"),
                TicketCode: ticketCode,
                PassengerName: passengerName,
                SeatNumbers: seatsDisplay,
                Route: routeSummary,
                DepartureTime: departureDisplay,
                CheckedInAt: checkInNowDisplay);
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Check-in failed for ticket {ticketId}.", ex);
            return new CheckInResult(
                CheckInOutcome.Invalid,
                false,
                T("Lỗi hệ thống khi cập nhật soát vé.", "System error while updating check-in."),
                TicketCode: ticketCode,
                ErrorReason: ex.Message);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private static async Task RollbackQuietlyAsync(SqlTransaction? transaction)
    {
        if (transaction?.Connection is null) return;
        try { await transaction.RollbackAsync(); }
        catch (InvalidOperationException ex) { LoggerService.LogWarning($"Transaction rollback skipped: {ex.Message}"); }
        catch (SqlException ex) { LoggerService.LogError("Transaction rollback failed.", ex); }
    }
}
