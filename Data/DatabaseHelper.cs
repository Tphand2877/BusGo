using BusGo.Models;
using BusGo.Services;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static BusGo.Services.UiText;

namespace BusGo.Data;

public static class DatabaseHelper
{
    /// <summary>Current password hash format: <c>PBKDF2-SHA256$iterations$salt$hash</c>.</summary>
    private const string PasswordHashPrefix = "PBKDF2-SHA256";

    /// <summary>First hashed format ever stored: <c>PBKDF2$iterations$salt$hash</c>.</summary>
    private const string LegacyPasswordHashPrefix = "PBKDF2";

    private const int PasswordIterations = 310_000;
    private const int PasswordSaltBytes = 16;
    private const int PasswordHashBytes = 32;

    private static string InvalidCredentialsMessage => T("Tên đăng nhập hoặc mật khẩu không đúng.", "Incorrect username or password.");
    private static string DatabaseUnavailableMessage => T("Không thể kết nối với cơ sở dữ liệu.", "Could not connect to the database.");

    private static string LockedOutMessage =>
        T("Bạn đã đăng nhập thất bại quá nhiều lần. Tài khoản này bị khóa trong 15 phút.", "Too many failed sign-in attempts. This account is locked for 15 minutes.");

    /// <summary>Consecutive failures that trigger a lockout.</summary>
    internal const int MaxFailedLoginAttempts = 5;

    /// <summary>How long an account stays locked after <see cref="MaxFailedLoginAttempts"/> failures.</summary>
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Derivation performed when no account matches, so a miss costs the same as a
    /// wrong password and cannot be used to enumerate accounts (AUD-D-011). The salt
    /// and digest are fixed and match no real password.
    /// </summary>
    private static readonly string DummyHash =
        $"{PasswordHashPrefix}${PasswordIterations}${Convert.ToBase64String(new byte[PasswordSaltBytes])}${Convert.ToBase64String(new byte[PasswordHashBytes])}";

    /// <summary>Set once the one-shot BusCompanies back-fill has run in this process.</summary>
    private static int _busCompaniesSynced;

    public static SqlConnection GetConnection() => new(DatabaseOptions.ConnectionString);

    /// <summary>
    /// Authenticates a customer or administrator.
    /// </summary>
    /// <remarks>
    /// After <see cref="MaxFailedLoginAttempts"/> consecutive failures the account is
    /// refused for <see cref="LockoutDuration"/> even when the password is correct
    /// (AUD-D-003). Every failure is logged by account id — never by the typed
    /// identifier, which may be an e-mail address. An unknown login pays the same
    /// key-derivation cost as a wrong password so the response time reveals nothing
    /// about whether the account exists (AUD-D-011).
    /// </remarks>
    public static async Task<(bool Success, UserSession? Account, string? Error)> AuthenticateAsync(string usernameOrEmail, string password)
    {
        const string sql = @"
SELECT TOP (1) AccountId, Username, FullName, Email, Phone, Role, PasswordHash, FailedLoginCount, LockoutEndsAt
FROM Accounts WHERE Username=@Login OR Email=@Login;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            int accountId, failedCount;
            string username, fullName, role, storedHash;
            string? email, phone;
            DateTime? lockoutEndsAt;
            await using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.Add("@Login", SqlDbType.NVarChar, 100).Value = usernameOrEmail;
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    VerifyPassword(password, DummyHash);
                    return (false, null, InvalidCredentialsMessage);
                }
                accountId = reader.GetInt32(0);
                username = reader.GetString(1);
                fullName = reader.GetString(2);
                email = reader.IsDBNull(3) ? null : reader.GetString(3);
                phone = reader.IsDBNull(4) ? null : reader.GetString(4);
                role = reader.IsDBNull(5) ? "Customer" : reader.GetString(5);
                storedHash = reader.GetString(6);
                failedCount = reader.GetInt32(7);
                lockoutEndsAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
            }

            if (lockoutEndsAt > DateTime.UtcNow)
            {
                LoggerService.LogWarning($"Sign-in refused for account {accountId}: locked until {lockoutEndsAt:u}.");
                return (false, null, LockedOutMessage);
            }

            if (!VerifyPassword(password, storedHash))
            {
                var attempts = await RecordFailedLoginAsync(connection, accountId);
                LoggerService.LogWarning(attempts >= MaxFailedLoginAttempts
                    ? $"Sign-in failed for account {accountId}: attempt {attempts}, account locked for {LockoutDuration.TotalMinutes:0} minute(s)."
                    : $"Sign-in failed for account {accountId}: attempt {attempts}.");
                return (false, null, attempts >= MaxFailedLoginAttempts ? LockedOutMessage : InvalidCredentialsMessage);
            }

            await ClearFailedLoginsAsync(connection, accountId, failedCount, lockoutEndsAt);

            // A successful login is the only moment the plaintext is available,
            // so it is also the only chance to migrate an outdated hash format.
            if (IsOutdatedHashFormat(storedHash))
                await UpgradeStoredHashAsync(connection, accountId, password, storedHash);

            return (true, new UserSession(accountId, username, fullName, email, phone, role), null);
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not authenticate: the account store is unavailable.", ex);
            return (false, null, DatabaseUnavailableMessage);
        }
    }

    /// <summary>
    /// Counts the failure and locks the account once the limit is reached.
    /// Returns the new consecutive-failure count.
    /// </summary>
    private static async Task<int> RecordFailedLoginAsync(SqlConnection connection, int accountId)
    {
        const string sql = @"
UPDATE Accounts
SET FailedLoginCount = FailedLoginCount + 1,
    LockoutEndsAt = CASE WHEN FailedLoginCount + 1 >= @Limit
                         THEN DATEADD(MINUTE, @Minutes, SYSUTCDATETIME())
                         ELSE LockoutEndsAt END
OUTPUT INSERTED.FailedLoginCount
WHERE AccountId = @AccountId;";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = MaxFailedLoginAttempts;
        command.Parameters.Add("@Minutes", SqlDbType.Int).Value = (int)LockoutDuration.TotalMinutes;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>Resets the counter after a successful sign-in, writing only when needed.</summary>
    private static async Task ClearFailedLoginsAsync(SqlConnection connection, int accountId, int failedCount, DateTime? lockoutEndsAt)
    {
        if (failedCount == 0 && lockoutEndsAt is null) return;

        const string sql = "UPDATE Accounts SET FailedLoginCount = 0, LockoutEndsAt = NULL WHERE AccountId = @AccountId;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<(bool Success, string? Error)> CreateAccountAsync(string username, string password, string fullName, string? phone, string? email)
    {
        if (!ValidateAccountInput(username, password, fullName, phone, email, out var validationError))
            return (false, validationError);
        const string sql = "INSERT INTO Accounts (Username, PasswordHash, FullName, Phone, Email) VALUES (@Username,@PasswordHash,@FullName,@Phone,@Email);";
        try
        {
            await using var connection = GetConnection(); await connection.OpenAsync(); await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username; command.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 255).Value = HashPassword(password);
            command.Parameters.Add("@FullName", SqlDbType.NVarChar, 100).Value = fullName; command.Parameters.Add("@Phone", SqlDbType.NVarChar, 20).Value = (object?)phone ?? DBNull.Value; command.Parameters.Add("@Email", SqlDbType.NVarChar, 100).Value = (object?)email ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(); return (true, null);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { return (false, T("Tên đăng nhập hoặc email này đã được đăng ký.", "That username or email is already registered.")); }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not create the account '{username}'.", ex);
            return (false, T("Không thể tạo tài khoản. Vui lòng thử lại.", "Could not create the account. Please try again."));
        }
    }

    public static async Task<(bool Success, UserSession? Account, string? Error)> UpdateProfileAsync(UserSession account, string fullName, string? email, string? phone)
    {
        var validationError = string.Empty;
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 100)
            return (false, null, T("Họ tên phải có từ 1 đến 100 ký tự.", "Full name must be 1-100 characters."));
        if (!ValidateContact(email, phone, out validationError))
            return (false, null, validationError);
        const string sql = "UPDATE Accounts SET FullName=@FullName, Email=@Email, Phone=@Phone WHERE AccountId=@AccountId;";
        try
        {
            await using var connection = GetConnection(); await connection.OpenAsync(); await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@FullName", SqlDbType.NVarChar, 100).Value = fullName; command.Parameters.Add("@Email", SqlDbType.NVarChar, 100).Value = (object?)email ?? DBNull.Value; command.Parameters.Add("@Phone", SqlDbType.NVarChar, 20).Value = (object?)phone ?? DBNull.Value; command.Parameters.Add("@AccountId", SqlDbType.Int).Value = account.AccountId;
            await command.ExecuteNonQueryAsync(); return (true, account with { FullName = fullName, Email = email, Phone = phone }, null);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { return (false, null, T("Địa chỉ email này đã được sử dụng.", "That email address is already in use.")); }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not update the profile of account {account.AccountId}.", ex);
            return (false, null, T("Không thể cập nhật thông tin tài khoản.", "Could not update the account details."));
        }
    }

    public static async Task<(bool Success, string? Error)> ChangePasswordAsync(int accountId, string currentPassword, string newPassword)
    {
        if (!ValidatePassword(newPassword, out var validationError))
            return (false, validationError);
        const string select = "SELECT PasswordHash FROM Accounts WHERE AccountId=@AccountId;";
        const string update = "UPDATE Accounts SET PasswordHash=@PasswordHash WHERE AccountId=@AccountId;";
        try
        {
            await using var connection = GetConnection(); await connection.OpenAsync(); await using var selectCommand = new SqlCommand(select, connection); selectCommand.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            var stored = await selectCommand.ExecuteScalarAsync() as string;
            if (stored is null || !VerifyPassword(currentPassword, stored)) return (false, T("Mật khẩu hiện tại không đúng.", "The current password is incorrect."));
            await using var updateCommand = new SqlCommand(update, connection); updateCommand.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 255).Value = HashPassword(newPassword); updateCommand.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId; await updateCommand.ExecuteNonQueryAsync(); return (true, null);
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not change the password of account {accountId}.", ex);
            return (false, T("Không thể đổi mật khẩu.", "Could not change the password."));
        }
    }

    /// <summary>
    /// Dashboard counters. <c>TotalTickets</c> includes reservations;
    /// <c>TotalSpent</c> is settled money only, so pending, expired or refunded
    /// payments contribute nothing to that amount. <c>UpcomingTrips</c> counts
    /// live tickets on trips that have not left.
    /// </summary>
    public static async Task<(int TotalTickets, int UpcomingTrips, decimal TotalSpent)> GetDashboardStatsAsync(int accountId)
    {
        const string sql = @"
SELECT COUNT(*),
       COALESCE(SUM(CASE WHEN tk.Status IN (N'Đã đặt', N'Đã thanh toán') AND tr.DepartureTime >= SYSUTCDATETIME() THEN 1 ELSE 0 END), 0),
       COALESCE(SUM(CASE WHEN tk.PaymentStatus = N'Đã thanh toán' THEN tk.TotalAmount ELSE 0 END), 0)
FROM Tickets tk
JOIN Trips tr ON tr.TripId = tk.TripId
WHERE tk.AccountId = @AccountId;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetDecimal(2));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not load dashboard counters for account {accountId}.", ex);
            throw new DataAccessException(ex);
        }
    }

    public static async Task<IReadOnlyList<PopularRouteItem>> GetPopularRoutesAsync(int top = 4)
    {
        const string sql = @"
SELECT TOP (@Top)
    r.RouteId,
    r.Origin,
    r.Destination,
    r.DistanceKm,
    COALESCE(MIN(t.Price), 120000) AS MinPrice,
    COALESCE(MAX(b.CompanyName), N'Sao Việt') AS CompanyName,
    COALESCE(MAX(bc.BrandColor), N'#0F4C81') AS BrandColor,
    COALESCE(MAX(bc.LogoText), N'SV') AS LogoText,
    COALESCE(MAX(r.IntermediateStops), N'') AS IntermediateStops
FROM Routes r
LEFT JOIN Trips t ON t.RouteId = r.RouteId
LEFT JOIN Buses b ON b.BusId = t.BusId
LEFT JOIN BusCompanies bc ON bc.CompanyName = b.CompanyName
GROUP BY r.RouteId, r.Origin, r.Destination, r.DistanceKm
ORDER BY COUNT(t.TripId) DESC, r.RouteId;";

        var list = new List<PopularRouteItem>();
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Top", SqlDbType.Int).Value = top;
            await using var reader = await command.ExecuteReaderAsync();
            int idx = 1;
            while (await reader.ReadAsync())
            {
                var routeId = reader.GetInt32(0);
                var origin = reader.GetString(1);
                var destination = reader.GetString(2);
                var distance = reader.IsDBNull(3) ? 100m : reader.GetDecimal(3);
                var minPrice = reader.GetDecimal(4);
                var companyName = reader.IsDBNull(5) ? "Sao Việt" : reader.GetString(5);
                var brandColor = reader.IsDBNull(6) ? "#0F4C81" : reader.GetString(6);
                var logoText = reader.IsDBNull(7) ? "SV" : reader.GetString(7);
                var intermediateStops = reader.IsDBNull(8) ? "" : reader.GetString(8);

                var hours = (int)(distance / 50m);
                var minutes = (int)(((distance / 50m) - hours) * 60m);
                var duration = hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
                var tag = idx switch { 1 => "Most Popular", 2 => "Express Daily", 3 => "Direct Route", _ => "Featured" };
                list.Add(new PopularRouteItem(routeId, origin, destination, duration, $"{minPrice:N0} đ", tag, minPrice,
                    companyName, brandColor, logoText, intermediateStops));
                idx++;
            }
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not load the popular routes.", ex);
            throw new DataAccessException(ex);
        }
        return list;
    }

    public static async Task<IReadOnlyList<string>> GetDistinctOriginsAsync()
    {
        const string sql = "SELECT DISTINCT Origin FROM Routes ORDER BY Origin;";
        return await GetLocationListAsync(sql);
    }

    public static async Task<IReadOnlyList<string>> GetDestinationsByOriginAsync(string? origin)
    {
        const string filteredSql = "SELECT DISTINCT Destination FROM Routes WHERE Origin = @Origin ORDER BY Destination;";
        const string allSql = "SELECT DISTINCT Destination FROM Routes ORDER BY Destination;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(string.IsNullOrWhiteSpace(origin) ? allSql : filteredSql, connection);
            if (!string.IsNullOrWhiteSpace(origin))
                command.Parameters.Add("@Origin", SqlDbType.NVarChar, 100).Value = origin.Trim();
            var list = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(reader.GetString(0));
            return list;
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not load the destination list.", ex);
            throw new DataAccessException(ex);
        }
    }

    private static async Task<IReadOnlyList<string>> GetLocationListAsync(string sql)
    {
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            var list = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(reader.GetString(0));
            return list;
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not load a location list.", ex);
            throw new DataAccessException(ex);
        }
    }

    public static string EscapeLikePattern(string term) => term
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);

    public static async Task<IReadOnlyList<TripSearchResult>> SearchTripsAsync(string? origin, string? destination, DateTime? date)
    {
        var originFilter = string.IsNullOrWhiteSpace(origin) ? "%" : $"%{EscapeLikePattern(origin.Trim())}%";
        var destFilter = string.IsNullOrWhiteSpace(destination) ? "%" : $"%{EscapeLikePattern(destination.Trim())}%";
        var dateClause = date.HasValue ? "AND CAST(t.DepartureTime AS date)=@Date" : "AND t.DepartureTime >= SYSUTCDATETIME()";
        var sql = $@"SELECT t.TripId, r.Origin, r.Destination, b.BusType, t.DepartureTime, t.ArrivalTime, t.Price,
                       b.SeatCount - (SELECT COUNT(DISTINCT ts2.SeatId)
                                      FROM TicketSeats ts2
                                      JOIN Tickets tk2 ON tk2.TicketId=ts2.TicketId
                                      WHERE tk2.TripId=t.TripId
                                        AND tk2.Status IN (N'Đã đặt',N'Đã thanh toán',N'Đã sử dụng')
                                        AND (tk2.PaymentStatus=N'Đã thanh toán' OR tk2.PaymentExpiresAt>SYSUTCDATETIME())),
                       COALESCE(b.CompanyName, N'Sao Việt') AS CompanyName,
                       COALESCE(bc.BrandColor, N'#0F4C81') AS BrandColor,
                       COALESCE(bc.LogoText, N'SV') AS LogoText,
                       COALESCE(r.IntermediateStops, N'') AS IntermediateStops,
                       COALESCE(b.DriverName, N'') AS DriverName,
                       COALESCE(b.DriverPhone, N'') AS DriverPhone,
                       COALESCE(b.LicensePlate, N'') AS LicensePlate
                FROM Trips t
                JOIN Routes r ON r.RouteId=t.RouteId
                JOIN Buses b ON b.BusId=t.BusId
                LEFT JOIN BusCompanies bc ON bc.CompanyName=b.CompanyName
                WHERE r.Origin LIKE @Origin AND r.Destination LIKE @Destination
                  AND t.Status = N'Scheduled' {dateClause}
                ORDER BY t.DepartureTime;";
        var results = new List<TripSearchResult>();
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Origin", SqlDbType.NVarChar, 100).Value = originFilter;
            command.Parameters.Add("@Destination", SqlDbType.NVarChar, 100).Value = destFilter;
            if (date.HasValue) command.Parameters.Add("@Date", SqlDbType.Date).Value = date.Value.Date;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(new TripSearchResult(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? "Standard" : reader.GetString(3),
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                    DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                    reader.GetDecimal(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? "Sao Việt" : reader.GetString(8),
                    reader.IsDBNull(9) ? "#0F4C81" : reader.GetString(9),
                    reader.IsDBNull(10) ? "SV" : reader.GetString(10),
                    reader.IsDBNull(11) ? "" : reader.GetString(11),
                    reader.IsDBNull(12) ? "" : reader.GetString(12),
                    reader.IsDBNull(13) ? "" : reader.GetString(13),
                    reader.IsDBNull(14) ? "" : reader.GetString(14)));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not search trips.", ex);
            throw new DataAccessException(ex);
        }
        return results;
    }

    /// <summary>
    /// Back-fills <c>BusCompanies</c> from operator-entered <c>Buses.CompanyName</c>
    /// values that predate the company table.
    /// </summary>
    /// <remarks>
    /// Runs at most once per process. It used to execute on every company-list read,
    /// so browsing took write locks, and its unlocked check-then-insert raised a
    /// swallowed unique violation under concurrency that silently shortened the list
    /// (AUD-B-013). Companies created after start-up come from
    /// <c>AdminDatabaseService.RegisterBusAsync</c>, which inserts them directly.
    /// </remarks>
    public static async Task EnsureBusCompaniesSyncedAsync()
    {
        if (Interlocked.Exchange(ref _busCompaniesSynced, 1) == 1) return;

        const string syncSql = @"
INSERT INTO BusCompanies (CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor)
SELECT DISTINCT TRIM(b.CompanyName),
       NULL,
       0,
       0,
       COALESCE(NULLIF(MAX(r.Origin + N' - ' + r.Destination), N''), N''),
       N'',
       N'',
       UPPER(SUBSTRING(TRIM(b.CompanyName), 1, 2)),
       N'#0F4C81'
FROM Buses b
LEFT JOIN Routes r ON r.RouteId = b.RouteId
WHERE b.CompanyName IS NOT NULL AND TRIM(b.CompanyName) <> ''
  AND NOT EXISTS (SELECT 1 FROM BusCompanies bc WHERE TRIM(bc.CompanyName) = TRIM(b.CompanyName))
GROUP BY TRIM(b.CompanyName);";

        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(syncSql, connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Could not synchronize bus companies.", ex);
        }
    }

    public static async Task<IReadOnlyList<BusCompanyItem>> GetBookingCompaniesAsync(string? origin, string? destination, DateTime? date = null)
    {
        await EnsureBusCompaniesSyncedAsync();

        var hasOrigin = !string.IsNullOrWhiteSpace(origin);
        var hasDest = !string.IsNullOrWhiteSpace(destination);
        var originFilter = hasOrigin ? $"%{EscapeLikePattern(origin!.Trim())}%" : "%";
        var destFilter = hasDest ? $"%{EscapeLikePattern(destination!.Trim())}%" : "%";
        var dateClause = date.HasValue ? "AND CAST(t.DepartureTime AS date) = @Date" : "";
        var sql = $@"
SELECT MIN(bc.CompanyId) AS CompanyId,
       TRIM(bc.CompanyName) AS CompanyName,
       COALESCE(MAX(bc.Hotline), N'') AS Hotline,
       COALESCE(MAX(bc.Rating), 0) AS Rating,
       COALESCE(MAX(bc.ReviewCount), 0) AS ReviewCount,
       COALESCE(MAX(bc.OperatingArea), N'') AS OperatingArea,
       COALESCE(MAX(bc.Description), N'') AS Description,
       COALESCE(MAX(bc.Amenities), N'') AS Amenities,
       COALESCE(MAX(bc.LogoText), UPPER(SUBSTRING(TRIM(bc.CompanyName), 1, 2))) AS LogoText,
       COALESCE(MAX(bc.BrandColor), N'#0F4C81') AS BrandColor,
       COALESCE(MIN(t.Price), 0) AS MinPrice,
       COUNT(DISTINCT t.TripId) AS ActiveTripsCount
FROM BusCompanies bc
LEFT JOIN Buses b ON TRIM(b.CompanyName) = TRIM(bc.CompanyName)
LEFT JOIN Trips t ON t.BusId = b.BusId AND t.DepartureTime >= SYSUTCDATETIME() {dateClause}
LEFT JOIN Routes r ON r.RouteId = t.RouteId
WHERE 1=1
  {(hasOrigin ? "AND (r.Origin LIKE @Origin OR bc.OperatingArea LIKE @Origin)" : "")}
  {(hasDest ? "AND (r.Destination LIKE @Dest OR bc.OperatingArea LIKE @Dest)" : "")}
GROUP BY TRIM(bc.CompanyName)
ORDER BY ActiveTripsCount DESC, TRIM(bc.CompanyName);";

        var list = new List<BusCompanyItem>();
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            if (hasOrigin) command.Parameters.Add("@Origin", SqlDbType.NVarChar, 100).Value = originFilter;
            if (hasDest) command.Parameters.Add("@Dest", SqlDbType.NVarChar, 100).Value = destFilter;
            if (date.HasValue) command.Parameters.Add("@Date", SqlDbType.Date).Value = date.Value.Date;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new BusCompanyItem(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? "" : reader.GetString(7),
                    reader.IsDBNull(8) ? "XE" : reader.GetString(8),
                    reader.IsDBNull(9) ? "#0F4C81" : reader.GetString(9),
                    reader.GetDecimal(10),
                    reader.GetInt32(11)));
            }
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not load booking companies.", ex);
            throw new DataAccessException(ex);
        }
        return list;
    }

    public static async Task<IReadOnlyList<BusCompanyItem>> GetBusCompaniesAsync()
    {
        await EnsureBusCompaniesSyncedAsync();
        const string sql = @"SELECT CompanyId, CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor
                             FROM BusCompanies
                             ORDER BY CompanyName;";
        var list = new List<BusCompanyItem>();
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new BusCompanyItem(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? "" : reader.GetString(7),
                    reader.IsDBNull(8) ? "SV" : reader.GetString(8),
                    reader.IsDBNull(9) ? "#0F4C81" : reader.GetString(9)));
            }
        }
        catch (SqlException ex)
        {
            LoggerService.LogError("Could not load bus companies.", ex);
            throw new DataAccessException(ex);
        }
        return list;
    }

    public static async Task<BusCompanyItem?> GetBusCompanyByNameAsync(string name)
    {
        const string sql = @"SELECT TOP 1 CompanyId, CompanyName, Hotline, Rating, ReviewCount, OperatingArea, Description, Amenities, LogoText, BrandColor
                             FROM BusCompanies
                             WHERE CompanyName = @Name OR CompanyName LIKE @Pattern;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Value = name;
            command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 100).Value = $"%{EscapeLikePattern(name.Trim())}%";
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new BusCompanyItem(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? "" : reader.GetString(7),
                    reader.IsDBNull(8) ? "SV" : reader.GetString(8),
                    reader.IsDBNull(9) ? "#0F4C81" : reader.GetString(9));
            }
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not find bus company '{name}'.", ex);
            throw new DataAccessException(ex);
        }
        return null;
    }

    public static async Task<IReadOnlyList<TripSearchResult>> GetTripsByCompanyAsync(string companyName, DateTime? date = null)
    {
        var dateClause = date.HasValue ? "AND CAST(t.DepartureTime AS date)=@Date" : "AND t.DepartureTime >= SYSUTCDATETIME()";
        var sql = $@"SELECT t.TripId, r.Origin, r.Destination, b.BusType, t.DepartureTime, t.ArrivalTime, t.Price,
                       b.SeatCount - (SELECT COUNT(DISTINCT ts2.SeatId)
                                      FROM TicketSeats ts2
                                      JOIN Tickets tk2 ON tk2.TicketId=ts2.TicketId
                                      WHERE tk2.TripId=t.TripId
                                        AND tk2.Status IN (N'Đã đặt',N'Đã thanh toán',N'Đã sử dụng')
                                        AND (tk2.PaymentStatus=N'Đã thanh toán' OR tk2.PaymentExpiresAt>SYSUTCDATETIME())),
                       COALESCE(b.CompanyName, N'Sao Việt') AS CompanyName,
                       COALESCE(bc.BrandColor, N'#0F4C81') AS BrandColor,
                       COALESCE(bc.LogoText, N'SV') AS LogoText,
                       COALESCE(r.IntermediateStops, N'') AS IntermediateStops,
                       COALESCE(b.DriverName, N'') AS DriverName,
                       COALESCE(b.DriverPhone, N'') AS DriverPhone,
                       COALESCE(b.LicensePlate, N'') AS LicensePlate
                FROM Trips t
                JOIN Routes r ON r.RouteId=t.RouteId
                JOIN Buses b ON b.BusId=t.BusId
                LEFT JOIN BusCompanies bc ON bc.CompanyName=b.CompanyName
                WHERE (b.CompanyName = @Company OR b.CompanyName LIKE @CompanyPattern)
                  AND t.Status = N'Scheduled' {dateClause}
                ORDER BY t.DepartureTime;";
        var results = new List<TripSearchResult>();
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Company", SqlDbType.NVarChar, 100).Value = companyName;
            command.Parameters.Add("@CompanyPattern", SqlDbType.NVarChar, 100).Value = $"%{EscapeLikePattern(companyName.Trim())}%";
            if (date.HasValue) command.Parameters.Add("@Date", SqlDbType.Date).Value = date.Value.Date;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new TripSearchResult(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? "Standard" : reader.GetString(3),
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                    DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                    reader.GetDecimal(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? "Sao Việt" : reader.GetString(8),
                    reader.IsDBNull(9) ? "#0F4C81" : reader.GetString(9),
                    reader.IsDBNull(10) ? "SV" : reader.GetString(10),
                    reader.IsDBNull(11) ? "" : reader.GetString(11),
                    reader.IsDBNull(12) ? "" : reader.GetString(12),
                    reader.IsDBNull(13) ? "" : reader.GetString(13),
                    reader.IsDBNull(14) ? "" : reader.GetString(14)));
            }
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not load trips for company '{companyName}'.", ex);
            throw new DataAccessException(ex);
        }
        return results;
    }

    public static async Task<IReadOnlyList<SeatInfo>> GetSeatsAsync(int tripId)
    {
        const string sql = @"SELECT s.SeatId,s.BusId,s.SeatNumber,
CONVERT(bit,CASE WHEN EXISTS(
    SELECT 1 FROM TicketSeats ts
    JOIN Tickets t ON t.TicketId=ts.TicketId
    WHERE t.TripId=@TripId AND ts.SeatId=s.SeatId
      AND t.Status IN(N'Đã đặt',N'Đã thanh toán',N'Đã sử dụng')
      AND (t.PaymentStatus=N'Đã thanh toán' OR t.PaymentExpiresAt>SYSUTCDATETIME())
) THEN 1 ELSE 0 END)
FROM Seats s JOIN Trips tr ON tr.BusId=s.BusId WHERE tr.TripId=@TripId ORDER BY s.SeatId;";
        var seats = new List<SeatInfo>();
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@TripId", SqlDbType.Int).Value = tripId;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                seats.Add(new SeatInfo(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3)));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not load the seat map for trip {tripId}.", ex);
            throw new DataAccessException(ex);
        }
        return seats;
    }

    /// <summary>
    /// Reserves all requested seats in one serializable transaction, creating a
    /// single <c>Tickets</c> row and one <c>TicketSeats</c> row per seat.
    /// This produces one ticket code, one payment and one customer-facing item.
    /// </summary>
    public static async Task<(bool Success, TicketDetails? Ticket, string? Error, bool QuoteChanged)> BookTicketsAsync(
        int accountId,
        int tripId,
        IReadOnlyCollection<int> seatIds,
        string passengerName,
        string? passengerPhone,
        decimal quotedTotal,
        int? quotedDiscountId)
    {
        if (CurrentUser.Account is not { } account || account.AccountId != accountId)
            return (false, null, T("Phiên đặt vé không hợp lệ. Vui lòng đăng nhập lại.", "Your booking session is invalid. Sign in again."), false);
        var requestedSeatIds = seatIds.Distinct().ToArray();
        if (requestedSeatIds.Length == 0)
            return (false, null, T("Vui lòng chọn ít nhất một ghế.", "Please select at least one seat."), false);
        if (requestedSeatIds.Length != seatIds.Count)
            return (false, null, T("Mỗi ghế chỉ được chọn một lần.", "Each seat can only be selected once."), false);

        try
        {
            await Services.PaymentService.ExpirePendingPaymentsAsync();
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not expire pending payments before booking (account={accountId}, trip={tripId}).", ex);
            return (false, null, T("Không thể truy cập dữ liệu thanh toán. Hãy kiểm tra kết nối cơ sở dữ liệu và các bản cập nhật cấu trúc.", "Could not reach the payment data. Check the database connection and migrations."), false);
        }

        // The unit of work below either commits or rolls back completely, so a
        // transient server fault — typically a deadlock over the seat range — can be
        // retried without risk of double-applying (AUD-B-006).
        try
        {
            return await SqlResilience.ExecuteAsync(
                () => BookSeatsCoreAsync(accountId, tripId, requestedSeatIds, passengerName, passengerPhone, quotedTotal, quotedDiscountId),
                $"book {requestedSeatIds.Length} seat(s) on trip {tripId}");
        }
        catch (SqlException ex)
        {
            // Every retry lost the race as well.
            LoggerService.LogError($"Could not book {requestedSeatIds.Length} seat(s) after retries (account={accountId}, trip={tripId}).", ex);
            return (false, null, T("Không thể đặt ghế vì hệ thống đang bận. Vui lòng thử lại.", "The seats could not be booked because the system is busy. Please try again."), false);
        }
    }

    private static async Task<(bool Success, TicketDetails? Ticket, string? Error, bool QuoteChanged)> BookSeatsCoreAsync(
        int accountId,
        int tripId,
        int[] requestedSeatIds,
        string passengerName,
        string? passengerPhone,
        decimal quotedTotal,
        int? quotedDiscountId)
    {

        // Keep the published per-seat fare and snapshot the authoritative discount separately.
        const string insertTicket = @"
INSERT INTO Tickets(TicketCode,TripId,AccountId,PassengerName,PassengerPhone,Price,SeatCount,Status,PaymentStatus,PaymentExpiresAt,
                    DiscountAmount,MembershipDiscountId,DiscountName,MembershipTier)
SELECT @Code,tr.TripId,@AccountId,@Name,@Phone,tr.Price,@SeatCount,N'Đã đặt',N'Chưa thanh toán',DATEADD(MINUTE,15,SYSUTCDATETIME()),
       @DiscountAmount,@DiscountId,@DiscountName,@MembershipTier
FROM Trips tr WHERE tr.TripId=@TripId;
SELECT CAST(SCOPE_IDENTITY() AS int);";

        // Insert one junction row per seat, guarded against double-booking.
        const string insertSeat = @"
INSERT INTO TicketSeats(TicketId,SeatId)
SELECT @TicketId,@SeatId
WHERE EXISTS(SELECT 1 FROM Seats s JOIN Trips tr ON tr.BusId=s.BusId WHERE tr.TripId=@TripId AND s.SeatId=@SeatId)
  AND NOT EXISTS(
    SELECT 1 FROM TicketSeats ts2
    JOIN Tickets x ON x.TicketId=ts2.TicketId
    WHERE x.TripId=@TripId AND ts2.SeatId=@SeatId
      AND x.Status IN(N'Đã đặt',N'Đã thanh toán',N'Đã sử dụng')
      AND (x.PaymentStatus=N'Đã thanh toán' OR x.PaymentExpiresAt>SYSUTCDATETIME()));";

        await using var connection = GetConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            decimal unitPrice;
            await using (var tripCommand = new SqlCommand(
                "SELECT Price FROM Trips WITH (UPDLOCK,HOLDLOCK) WHERE TripId=@TripId AND Status=N'Scheduled' AND DepartureTime>SYSUTCDATETIME();",
                connection, transaction))
            {
                tripCommand.Parameters.Add("@TripId", SqlDbType.Int).Value = tripId;
                if (await tripCommand.ExecuteScalarAsync() is not decimal price)
                {
                    await transaction.RollbackAsync();
                    return (false, null, T("Chuyến này không còn nhận đặt vé.", "This departure is no longer available for booking."), false);
                }
                unitPrice = price;
            }
            var quote = await MembershipDatabaseService.GetQuoteAsync(
                connection, transaction, accountId, unitPrice * requestedSeatIds.Length);
            if (quote.TotalAmount != quotedTotal || quote.DiscountId != quotedDiscountId)
            {
                await transaction.RollbackAsync();
                return (false, null, T("Giá vé hoặc ưu đãi đã thay đổi. Vui lòng kiểm tra lại tổng tiền và xác nhận lần nữa.",
                    "The fare or discount has changed. Review the updated total and confirm again."), true);
            }
            // 1. Create a single ticket.
            var code = $"BT-{DateTime.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
            int ticketId;
            await using (var ticketCmd = new SqlCommand(insertTicket, connection, transaction))
            {
                ticketCmd.Parameters.Add("@Code", SqlDbType.NVarChar, 30).Value = code;
                ticketCmd.Parameters.Add("@TripId", SqlDbType.Int).Value = tripId;
                ticketCmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
                ticketCmd.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Value = passengerName;
                ticketCmd.Parameters.Add("@Phone", SqlDbType.NVarChar, 20).Value = (object?)passengerPhone ?? DBNull.Value;
                ticketCmd.Parameters.Add("@SeatCount", SqlDbType.Int).Value = requestedSeatIds.Length;
                var discountAmount = ticketCmd.Parameters.Add("@DiscountAmount", SqlDbType.Decimal);
                discountAmount.Precision = 18;
                discountAmount.Scale = 2;
                discountAmount.Value = quote.DiscountAmount;
                ticketCmd.Parameters.Add("@DiscountId", SqlDbType.Int).Value = (object?)quote.DiscountId ?? DBNull.Value;
                ticketCmd.Parameters.Add("@DiscountName", SqlDbType.NVarChar, 100).Value = (object?)quote.DiscountName ?? DBNull.Value;
                ticketCmd.Parameters.Add("@MembershipTier", SqlDbType.NVarChar, 16).Value =
                    quote.DiscountId.HasValue ? quote.Tier.ToString() : DBNull.Value;
                var scalar = await ticketCmd.ExecuteScalarAsync();
                if (scalar is null or DBNull)
                {
                    await transaction.RollbackAsync();
                    return (false, null, T("Không tìm thấy chuyến xe.", "The trip was not found."), false);
                }
                ticketId = (int)scalar;
            }

            // 2. Link each seat.
            foreach (var seatId in requestedSeatIds)
            {
                await using var seatCmd = new SqlCommand(insertSeat, connection, transaction);
                seatCmd.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;
                seatCmd.Parameters.Add("@SeatId", SqlDbType.Int).Value = seatId;
                seatCmd.Parameters.Add("@TripId", SqlDbType.Int).Value = tripId;
                if (await seatCmd.ExecuteNonQueryAsync() == 0)
                {
                    await transaction.RollbackAsync();
                    return (false, null, T("Một trong các ghế đã chọn vừa được đặt hoặc không hợp lệ cho chuyến xe này.", "One of the selected seats has just been taken or is not valid for this trip."), false);
                }
            }

            // 3. Read back the complete ticket.
            var select = TicketProjection + " WHERE tk.TicketId = @TicketId;";
            await using var selectCommand = new SqlCommand(select, connection, transaction);
            selectCommand.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;

            TicketDetails? ticket = null;
            await using (var reader = await selectCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                    ticket = ReadTicket(reader);
            }

            if (ticket is null)
            {
                await transaction.RollbackAsync();
                return (false, null, T("Không thể tải các ghế đã chọn sau khi đặt vé.", "The selected seats could not be loaded after booking."), false);
            }

            await transaction.CommitAsync();
            return (true, ticket, null, false);
        }
        catch (SqlException ex) when (SqlResilience.IsTransient(ex))
        {
            // Rolled back completely, so the caller's retry can start clean. If the
            // retries run out the exception surfaces from BookTicketsAsync's wrapper.
            await RollbackQuietlyAsync(transaction);
            throw;
        }
        catch (SqlException ex)
        {
            await RollbackQuietlyAsync(transaction);
            LoggerService.LogError($"Could not book {requestedSeatIds.Length} seat(s) (account={accountId}, trip={tripId}).", ex);

            // 2601/2627: the (TripId, SeatId) uniqueness object refused the write.
            // 51001/51002: a TicketSeats trigger refused it. Both mean the same thing
            // to the customer, and neither is a connection problem.
            return ex.Number is 2601 or 2627 or 51001 or 51002
                ? (false, null, T("Một trong các ghế đã chọn vừa được đặt hoặc không hợp lệ cho chuyến xe này.", "One of the selected seats has just been taken or is not valid for this trip."), false)
                : (false, null, T("Không thể đặt các ghế đã chọn. Hãy kiểm tra kết nối cơ sở dữ liệu và dữ liệu chuyến xe.", "Could not book the selected seats. Check the database connection and trip data."), false);
        }
    }

    /// <summary>
    /// Rolls back unless the server already did. A trigger or an aborting error can
    /// end the transaction server-side, after which <see cref="SqlTransaction.RollbackAsync"/>
    /// throws <see cref="InvalidOperationException"/> and would mask the real failure.
    /// </summary>
    private static async Task RollbackQuietlyAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // Already rolled back by the server; nothing left to undo.
        }
    }

    /// <summary>
    /// Releases an unpaid seat hold the customer owns.
    /// </summary>
    /// <remarks>
    /// A settled ticket is delegated to <see cref="RequestRefundAsync"/>. Both methods
    /// used to implement the paid-ticket release with slightly different preconditions
    /// while only the refund path was reachable from the UI, so the two were free to
    /// drift apart during maintenance (AUD-C-015).
    /// </remarks>
    public static async Task<(bool Success, string? Error)> CancelTicketAsync(int ticketId, int accountId)
    {
        if (await IsSettledTicketAsync(ticketId, accountId))
            return await RequestRefundAsync(ticketId, accountId);

        const string sql = @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @Status NVARCHAR(20), @Departure DATETIME2;
SELECT @Status = tk.Status, @Departure = tr.DepartureTime
FROM dbo.Tickets tk WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.Trips tr ON tr.TripId = tk.TripId
WHERE tk.TicketId = @TicketId AND tk.AccountId = @AccountId;

IF @Status IS NULL
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 1;
END
ELSE IF @Status NOT IN (N'Đã đặt', N'Đã thanh toán')
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 2;
END
ELSE IF @Departure <= SYSUTCDATETIME()
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 3;
END
ELSE
BEGIN
    UPDATE dbo.Tickets SET Status = N'Đã hủy', CancelledAt = SYSUTCDATETIME(), PaymentExpiresAt = NULL
    WHERE TicketId = @TicketId;

    /* Unpaid hold: fail the pending payment so the ledger keeps no phantom entry for
       a released seat. A settled ticket never reaches here — it is delegated to
       RequestRefundAsync before this statement runs. */
    UPDATE dbo.Payments SET Status = N'Failed', Notes = COALESCE(Notes, N'Cancelled by the customer before payment.')
    WHERE TicketId = @TicketId AND Status = N'Pending';

    COMMIT TRANSACTION;
    SELECT 0;
END;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            var code = Convert.ToInt32(await command.ExecuteScalarAsync());
            return code switch
            {
                0 => (true, null),
                1 => (false, T("Không tìm thấy vé.", "Ticket not found.")),
                2 => (false, T("Vé này đã bị hủy hoặc đã sử dụng và không thể hủy lại.", "This ticket is already cancelled or used and cannot be cancelled again.")),
                _ => (false, T("Chuyến xe này đã khởi hành và không thể hủy nữa.", "This trip has already departed and can no longer be cancelled."))
            };
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not cancel ticket {ticketId} for account {accountId}.", ex);
            return (false, T("Hiện không thể hủy vé.", "Could not cancel the ticket right now."));
        }
    }

    /// <summary>Whether the ticket belongs to the account and has already been paid for.</summary>
    private static async Task<bool> IsSettledTicketAsync(int ticketId, int accountId)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM dbo.Tickets
    WHERE TicketId = @TicketId AND AccountId = @AccountId AND PaymentStatus = N'Đã thanh toán')
THEN 1 ELSE 0 END;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not read the payment state of ticket {ticketId}.", ex);
            throw new DataAccessException(ex);
        }
    }

    /// <summary>
    /// Customer half of the refund flow: releases a settled ticket and stamps
    /// <c>Payments.RefundRequestedAt</c> so an administrator can approve the
    /// payout. Approval itself is <c>PaymentService.ApproveRefundAsync</c>.
    /// </summary>
    public static async Task<(bool Success, string? Error)> RequestRefundAsync(int ticketId, int accountId)
    {
        const string sql = @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @Status NVARCHAR(20), @PaymentStatus NVARCHAR(20), @Departure DATETIME2;
SELECT @Status = tk.Status, @PaymentStatus = tk.PaymentStatus, @Departure = tr.DepartureTime
FROM dbo.Tickets tk WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.Trips tr ON tr.TripId = tk.TripId
WHERE tk.TicketId = @TicketId AND tk.AccountId = @AccountId;

IF @Status IS NULL
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 1;
END
ELSE IF @Status = N'Đã hủy'
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 2;
END
ELSE IF @Status = N'Đã sử dụng'
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 3;
END
ELSE IF @Status <> N'Đã thanh toán' OR @PaymentStatus <> N'Đã thanh toán'
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 4;
END
ELSE IF @Departure <= SYSUTCDATETIME()
BEGIN
    ROLLBACK TRANSACTION;
    SELECT 5;
END
ELSE
BEGIN
    UPDATE dbo.Tickets SET Status = N'Đã hủy', CancelledAt = SYSUTCDATETIME(), PaymentExpiresAt = NULL
    WHERE TicketId = @TicketId;

    UPDATE dbo.Payments SET RefundRequestedAt = SYSUTCDATETIME()
    WHERE TicketId = @TicketId AND Status = N'Success' AND RefundRequestedAt IS NULL;

    COMMIT TRANSACTION;
    SELECT 0;
END;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            var code = Convert.ToInt32(await command.ExecuteScalarAsync());
            return code switch
            {
                0 => (true, null),
                1 => (false, T("Không tìm thấy vé.", "Ticket not found.")),
                2 => (false, T("Vé này đã bị hủy.", "This ticket is already cancelled.")),
                3 => (false, T("Vé này đã được sử dụng.", "This ticket has already been used.")),
                4 => (false, T("Chỉ vé đã thanh toán mới có thể được hoàn tiền.", "Only paid tickets can be refunded.")),
                _ => (false, T("Chuyến xe này đã khởi hành; không thể yêu cầu hoàn tiền nữa.", "This trip has already departed; a refund can no longer be requested."))
            };
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not request a refund for ticket {ticketId} (account {accountId}).", ex);
            return (false, T("Hiện không thể gửi yêu cầu hoàn tiền.", "Could not submit the refund request right now."));
        }
    }

    public static async Task<IReadOnlyList<TicketDetails>> GetTicketsAsync(int accountId, bool history)
    {
        const string sql = TicketProjection + @"
WHERE tk.AccountId = @AccountId
  AND (
    (@History = 1 AND (tk.Status IN (N'Đã sử dụng', N'Đã hủy') OR (tk.Status IN (N'Đã đặt', N'Đã thanh toán') AND tr.DepartureTime < SYSUTCDATETIME())))
    OR
    (@History = 0 AND tk.Status IN (N'Đã đặt', N'Đã thanh toán') AND tr.DepartureTime >= SYSUTCDATETIME())
  )
ORDER BY tr.DepartureTime DESC;";
        var list = new List<TicketDetails>();
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            command.Parameters.Add("@History", SqlDbType.Bit).Value = history;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(ReadTicket(reader));
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not load the {(history ? "ticket history" : "active tickets")} of account {accountId}.", ex);
            throw new DataAccessException(ex);
        }
        return list;
    }


    public static async Task<(int TotalCompleted, decimal TotalDistanceKm)> GetHistoryStatsAsync(int accountId)
    {
        const string sql = @"
SELECT COUNT(*),
       COALESCE(SUM(r.DistanceKm), 0)
FROM Tickets tk
JOIN Trips tr ON tr.TripId = tk.TripId
JOIN Routes r ON r.RouteId = tr.RouteId
WHERE tk.AccountId = @AccountId
  AND (tk.Status = N'Đã sử dụng' OR (tk.Status IN (N'Đã đặt', N'Đã thanh toán') AND tr.DepartureTime < SYSUTCDATETIME()));";

        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (reader.GetInt32(0), reader.GetDecimal(1));
            }
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not load the travel history statistics of account {accountId}.", ex);
            throw new DataAccessException(ex);
        }
        return (0, 0m);
    }

    public static async Task<TicketDetails?> GetTicketByCodeAsync(string ticketCode)
    {
        if (string.IsNullOrWhiteSpace(ticketCode)) return null;
        var select = TicketProjection + " WHERE tk.TicketCode = @Code;";
        try
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();
            await using var command = new SqlCommand(select, connection);
            command.Parameters.Add("@Code", SqlDbType.NVarChar, 30).Value = ticketCode.Trim();
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return ReadTicket(reader);
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not load ticket by code '{ticketCode}'.", ex);
            throw new DataAccessException(ex);
        }
        return null;
    }


    /// <summary>Column list shared by every <see cref="TicketDetails"/> query.</summary>
    private const string TicketProjection = @"
SELECT tk.TicketId,
       tk.TicketCode,
       r.Origin + N' → ' + r.Destination,
       tr.DepartureTime,
       tr.ArrivalTime,
       b.BusType,
       COALESCE((SELECT STRING_AGG(s.SeatNumber, N', ') WITHIN GROUP (ORDER BY s.SeatId)
                 FROM TicketSeats ts JOIN Seats s ON s.SeatId = ts.SeatId
                 WHERE ts.TicketId = tk.TicketId), N'—'),
       tk.Price,
       tk.Status,
       tk.BookingTime,
       r.Origin,
       r.Destination,
       COALESCE(r.DistanceKm, 0),
       tk.PassengerName,
       tk.PassengerPhone,
       b.LicensePlate,
       tk.PaymentStatus,
       tk.PaymentExpiresAt,
       tk.UsedAt,
       tk.CancelledAt,
       tk.SeatCount,
       COALESCE(b.CompanyName, N''),
       COALESCE(b.DriverName, N''),
       COALESCE((SELECT bc.Hotline FROM BusCompanies bc WHERE bc.CompanyName=b.CompanyName), N''),
       COALESCE((SELECT TOP(1) p.PaymentMethod FROM Payments p WHERE p.TicketId=tk.TicketId ORDER BY p.PaymentId DESC), N''),
       COALESCE((SELECT SUM(p.Amount) FROM Payments p WHERE p.TicketId=tk.TicketId AND p.Status=N'Refunded'), 0),
       tk.DiscountAmount,
       tk.MembershipDiscountId,
       tk.DiscountName,
       tk.MembershipTier
FROM Tickets tk
JOIN Trips tr ON tr.TripId = tk.TripId
JOIN Routes r ON r.RouteId = tr.RouteId
JOIN Buses b ON b.BusId = tr.BusId";

    private static TicketDetails ReadTicket(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
        DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
        reader.IsDBNull(5) ? "" : reader.GetString(5),
        reader.GetString(6),
        reader.GetDecimal(7),
        reader.GetString(8),
        DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc),
        reader.GetString(10),
        reader.GetString(11),
        reader.GetDecimal(12))
    {
        PassengerName = reader.GetString(13),
        PassengerPhone = reader.IsDBNull(14) ? null : reader.GetString(14),
        LicensePlate = reader.GetString(15),
        PaymentStatus = reader.GetString(16),
        PaymentExpiresAt = reader.IsDBNull(17) ? null : DateTime.SpecifyKind(reader.GetDateTime(17), DateTimeKind.Utc),
        UsedAt = reader.IsDBNull(18) ? null : DateTime.SpecifyKind(reader.GetDateTime(18), DateTimeKind.Utc),
        CancelledAt = reader.IsDBNull(19) ? null : DateTime.SpecifyKind(reader.GetDateTime(19), DateTimeKind.Utc),
        SeatCount = reader.GetInt32(20),
        CompanyName = reader.GetString(21),
        DriverName = reader.GetString(22),
        CompanyHotline = reader.GetString(23),
        PaymentMethod = reader.GetString(24),
        RefundAmount = reader.GetDecimal(25),
        DiscountAmount = reader.GetDecimal(26),
        MembershipDiscountId = reader.IsDBNull(27) ? null : reader.GetInt32(27),
        DiscountName = reader.IsDBNull(28) ? null : reader.GetString(28),
        MembershipTier = reader.IsDBNull(29) ? null : Enum.Parse<MemberTier>(reader.GetString(29))
    };

    internal static string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(PasswordSaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, PasswordHashBytes);
        return $"{PasswordHashPrefix}${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Accepts the current self-describing format and the first PBKDF2 format
    /// (<c>PBKDF2$...</c>); the latter is re-hashed on the next successful login,
    /// see <see cref="AuthenticateAsync"/>.
    /// </summary>
    /// <remarks>
    /// Anything that is not a derived hash is rejected. Plaintext values used to
    /// authenticate here, which meant that writing an arbitrary string into
    /// <c>Accounts.PasswordHash</c> yielded a working password for that account
    /// (AUD-D-002). No stored value is modified by this check: an account whose
    /// hash is not in a recognised format needs an administrative password reset.
    /// </remarks>
    internal static bool VerifyPassword(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(stored))
            return false;

        if (!TrySplitDerivedHash(stored, out var iterations, out var salt, out var expected))
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// True when the stored value is not the current format at the current
    /// work factor: plaintext, the first <c>PBKDF2$</c> format, or a hash
    /// derived with fewer iterations than <see cref="PasswordIterations"/>.
    /// </summary>
    private static bool IsOutdatedHashFormat(string stored)
    {
        if (!stored.StartsWith($"{PasswordHashPrefix}$", StringComparison.Ordinal))
            return true;
        return !TrySplitDerivedHash(stored, out var iterations, out _, out _) || iterations < PasswordIterations;
    }

    private static bool LooksLikeDerivedHash(string stored) =>
        stored.StartsWith($"{PasswordHashPrefix}$", StringComparison.Ordinal) ||
        stored.StartsWith($"{LegacyPasswordHashPrefix}$", StringComparison.Ordinal);

    private static bool TrySplitDerivedHash(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (!LooksLikeDerivedHash(stored))
            return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out iterations) || iterations <= 0)
            return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length > 0 && hash.Length > 0;
    }

    private static async Task UpgradeStoredHashAsync(SqlConnection connection, int accountId, string password, string storedHash)
    {
        const string sql = "UPDATE Accounts SET PasswordHash=@NewHash WHERE AccountId=@AccountId AND PasswordHash=@OldHash;";
        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@NewHash", SqlDbType.NVarChar, 255).Value = HashPassword(password);
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            command.Parameters.Add("@OldHash", SqlDbType.NVarChar, 255).Value = storedHash;
            if (await command.ExecuteNonQueryAsync() > 0)
                LoggerService.LogInfo($"Upgraded the stored password of account {accountId} to {PasswordHashPrefix} with {PasswordIterations} iterations.");
        }
        catch (SqlException ex)
        {
            LoggerService.LogWarning($"Could not upgrade the stored password format of account {accountId}: {ex.Message}");
        }
    }

    private static bool ValidateAccountInput(string username, string password, string fullName, string? phone, string? email, out string error)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 50) { error = T("Tên đăng nhập phải có từ 1 đến 50 ký tự.", "Username must be 1-50 characters."); return false; }
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 100) { error = T("Họ tên phải có từ 1 đến 100 ký tự.", "Full name must be 1-100 characters."); return false; }
        if (!ValidatePassword(password, out error)) return false;
        return ValidateContact(email, phone, out error);
    }

    /// <summary>
    /// The single password policy. Used by registration, self-service change and the
    /// administrative reset, which previously enforced length only (AUD-D-013).
    /// </summary>
    internal static bool ValidatePassword(string password, out string error)
    {
        if (password.Length < 8 || password.Length > 128 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
        { error = T("Mật khẩu phải có từ 8 đến 128 ký tự và bao gồm chữ hoa, chữ thường và chữ số.", "Password must be 8-128 characters and include an uppercase letter, a lowercase letter and a digit."); return false; }
        error = string.Empty; return true;
    }

    private static bool ValidateContact(string? email, string? phone, out string error)
    {
        if (!string.IsNullOrWhiteSpace(email) && !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        { error = T("Địa chỉ email không hợp lệ.", "Email address is not valid."); return false; }
        if (!string.IsNullOrWhiteSpace(phone) && !Regex.IsMatch(phone, @"^\+?[0-9\s-]{8,20}$"))
        { error = T("Số điện thoại không hợp lệ.", "Phone number is not valid."); return false; }
        error = string.Empty; return true;
    }
}
