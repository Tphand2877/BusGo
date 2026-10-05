using System.Data;
using System.Globalization;
using BusGo.Models;
using BusGo.Services;
using Microsoft.Data.SqlClient;
using static BusGo.Services.UiText;

namespace BusGo.Data;

public static class MembershipDatabaseService
{
    public static Task<IReadOnlyList<MembershipTierInfo>> GetTierDefinitionsAsync() =>
        WithConnectionAsync("load membership tiers", connection => ReadTiersAsync(connection, null));

    public static Task<MembershipOverview> GetOverviewAsync(int accountId) =>
        WithCustomerAsync(accountId, true, async (connection, transaction) =>
        {
            var tiers = await ReadTiersAsync(connection, transaction);
            decimal qualifiedSpend;
            MemberTier tier;
            await using (var command = new SqlCommand(
                "SELECT QualifiedSpend, TierCode FROM dbo.CustomerMemberships WHERE AccountId = @AccountId;", connection, transaction))
            {
                command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) throw AccessDenied();
                qualifiedSpend = reader.GetDecimal(0);
                tier = ParseTier(reader.GetString(1));
            }

            var current = tiers.Single(t => t.Tier == tier);
            var next = tiers.FirstOrDefault(t => t.MinimumSpend > current.MinimumSpend);
            const string discountsSql = @"
SELECT d.DiscountId, d.Name, d.[Percent], d.StartsAt, d.EndsAt, d.IsActive, dt.TierCode
FROM dbo.MembershipDiscounts d
JOIN dbo.MembershipDiscountTiers dt ON dt.DiscountId = d.DiscountId
WHERE d.IsActive = 1 AND d.EndsAt > SYSUTCDATETIME()
  AND EXISTS (SELECT 1 FROM dbo.MembershipDiscountTiers eligible
              WHERE eligible.DiscountId = d.DiscountId AND eligible.TierCode = @Tier)
ORDER BY d.[Percent] DESC, d.DiscountId, dt.TierCode;";
            await using var discountsCommand = new SqlCommand(discountsSql, connection, transaction);
            discountsCommand.Parameters.Add("@Tier", SqlDbType.NVarChar, 16).Value = tier.ToString();
            var discounts = await ReadDiscountsAsync(discountsCommand);
            var unread = await ReadUnreadCountAsync(connection, transaction, accountId);
            return new MembershipOverview(qualifiedSpend, current, next, tiers, discounts, unread);
        });

    public static Task<IReadOnlyList<MemberDiscount>> GetDiscountsAsync()
    {
        var adminId = RequireAdmin();
        return WithConnectionAsync("load membership discounts", async connection =>
        {
            await RequireDatabaseRoleAsync(connection, null, adminId, "Admin");
            const string sql = @"
SELECT d.DiscountId, d.Name, d.[Percent], d.StartsAt, d.EndsAt, d.IsActive, dt.TierCode
FROM dbo.MembershipDiscounts d
JOIN dbo.MembershipDiscountTiers dt ON dt.DiscountId = d.DiscountId
ORDER BY d.DiscountId DESC, dt.TierCode;";
            await using var command = new SqlCommand(sql, connection);
            return await ReadDiscountsAsync(command);
        });
    }

    public static Task<DiscountCreationResult> CreateDiscountAsync(int adminId, MembershipDiscountInput input)
    {
        RequireAdmin(adminId);
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
            return Task.FromResult(new DiscountCreationResult(false, 0, 0,
                T("Tên ưu đãi phải có từ 1 đến 100 ký tự.", "Offer name must contain 1–100 characters.")));
        if (input.Percent is < 0.01m or > 99m || decimal.Round(input.Percent, 2) != input.Percent)
            return Task.FromResult(new DiscountCreationResult(false, 0, 0,
                T("Mức giảm phải từ 0,01% đến 99% và có tối đa hai chữ số thập phân.", "Discount must be 0.01–99% with at most two decimal places.")));
        if (input.StartsAt.Kind != DateTimeKind.Utc || input.EndsAt.Kind != DateTimeKind.Utc || input.EndsAt <= input.StartsAt)
            return Task.FromResult(new DiscountCreationResult(false, 0, 0,
                T("Thời gian phải ở UTC và kết thúc sau khi bắt đầu.", "Times must be UTC and the end must be later than the start.")));
        if (input.TargetTiers is null || input.TargetTiers.Count == 0 ||
            input.TargetTiers.Any(tier => tier is not (MemberTier.Bronze or MemberTier.Silver or MemberTier.Gold or MemberTier.Diamond)))
            return Task.FromResult(new DiscountCreationResult(false, 0, 0,
                T("Chọn ít nhất một hạng Đồng, Bạc, Vàng hoặc Kim cương.", "Select at least one Bronze, Silver, Gold or Diamond tier.")));
        var targets = input.TargetTiers.Distinct().Order().ToArray();

        return WithConnectionAsync("create membership discount", async connection =>
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await RequireDatabaseRoleAsync(connection, transaction, adminId, "Admin");
            const string insertSql = @"
INSERT INTO dbo.MembershipDiscounts (Name, [Percent], StartsAt, EndsAt, CreatedByAccountId)
OUTPUT inserted.DiscountId
VALUES (@Name, @Percent, @StartsAt, @EndsAt, @AdminId);";
            int discountId;
            await using (var command = new SqlCommand(insertSql, connection, transaction))
            {
                command.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Value = name;
                var percent = command.Parameters.Add("@Percent", SqlDbType.Decimal);
                percent.Precision = 5;
                percent.Scale = 2;
                percent.Value = input.Percent;
                command.Parameters.Add("@StartsAt", SqlDbType.DateTime2).Value = input.StartsAt;
                command.Parameters.Add("@EndsAt", SqlDbType.DateTime2).Value = input.EndsAt;
                command.Parameters.Add("@AdminId", SqlDbType.Int).Value = adminId;
                discountId = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }
            await using (var command = new SqlCommand(
                "INSERT INTO dbo.MembershipDiscountTiers (DiscountId, TierCode) VALUES (@DiscountId, @Tier);", connection, transaction))
            {
                command.Parameters.Add("@DiscountId", SqlDbType.Int).Value = discountId;
                var tierParameter = command.Parameters.Add("@Tier", SqlDbType.NVarChar, 16);
                foreach (var tier in targets)
                {
                    tierParameter.Value = tier.ToString();
                    await command.ExecuteNonQueryAsync();
                }
            }
            // Announce future-start campaigns immediately, but never announce expired offers.
            const string notifySql = @"
INSERT INTO dbo.MembershipNotifications (AccountId, DiscountId, TierCode)
SELECT cm.AccountId, d.DiscountId, cm.TierCode
FROM dbo.CustomerMemberships cm
JOIN dbo.MembershipDiscountTiers dt ON dt.TierCode = cm.TierCode AND dt.DiscountId = @DiscountId
JOIN dbo.MembershipDiscounts d ON d.DiscountId = dt.DiscountId
WHERE d.IsActive = 1 AND d.EndsAt > SYSUTCDATETIME()
  AND NOT EXISTS (SELECT 1 FROM dbo.MembershipNotifications n WITH (UPDLOCK, HOLDLOCK)
                  WHERE n.AccountId = cm.AccountId AND n.DiscountId = d.DiscountId);
SELECT CONVERT(INT, @@ROWCOUNT);";
            int notified;
            await using (var command = new SqlCommand(notifySql, connection, transaction))
            {
                command.Parameters.Add("@DiscountId", SqlDbType.Int).Value = discountId;
                notified = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }
            await AuditLog.WriteAsync(connection, transaction, "MembershipDiscountCreated", "MembershipDiscount",
                discountId.ToString(CultureInfo.InvariantCulture),
                $"Name={name}; Percent={input.Percent.ToString(CultureInfo.InvariantCulture)}; Tiers={string.Join(",", targets)}; Notified={notified}.");
            await transaction.CommitAsync();
            return new DiscountCreationResult(true, discountId, notified, null);
        });
    }

    public static Task<(bool Success, string? Error)> DeactivateDiscountAsync(int adminId, int discountId)
    {
        RequireAdmin(adminId);
        return WithConnectionAsync<(bool Success, string? Error)>("deactivate membership discount", async connection =>
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await RequireDatabaseRoleAsync(connection, transaction, adminId, "Admin");
            await using var command = new SqlCommand(
                "UPDATE dbo.MembershipDiscounts SET IsActive = 0 WHERE DiscountId = @DiscountId AND IsActive = 1;", connection, transaction);
            command.Parameters.Add("@DiscountId", SqlDbType.Int).Value = discountId;
            if (await command.ExecuteNonQueryAsync() != 1)
                return (false, T("Ưu đãi không tồn tại hoặc đã ngừng hoạt động.", "The offer does not exist or is already inactive."));
            await AuditLog.WriteAsync(connection, transaction, "MembershipDiscountDeactivated", "MembershipDiscount",
                discountId.ToString(CultureInfo.InvariantCulture));
            await transaction.CommitAsync();
            return (true, null);
        });
    }

    public static Task<BookingDiscountQuote> GetQuoteAsync(int accountId, decimal baseFare)
    {
        var account = CurrentUser.Account;
        if (account is null || account.AccountId != accountId || account.Role is not ("Customer" or "Admin"))
            throw AccessDenied();
        return WithConnectionAsync("quote membership discount", async connection =>
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            await RequireDatabaseRoleAsync(connection, transaction, accountId, account.Role);
            var quote = await GetQuoteAsync(connection, transaction, accountId, baseFare);
            await transaction.CommitAsync();
            return quote;
        });
    }

    // This overload is deliberately bound to the booking transaction: membership,
    // campaign and fare snapshot cannot be read on a second connection.
    internal static async Task<BookingDiscountQuote> GetQuoteAsync(
        SqlConnection connection, SqlTransaction? transaction, int accountId, decimal baseFare)
    {
        if (baseFare < 0 || decimal.Round(baseFare, 2) != baseFare)
            throw new ArgumentOutOfRangeException(nameof(baseFare));
        const string sql = @"
DECLARE @Now DATETIME2 = SYSUTCDATETIME();
SELECT a.Role, cm.TierCode, offer.DiscountId, offer.Name, offer.[Percent]
FROM dbo.Accounts a
LEFT JOIN dbo.CustomerMemberships cm ON cm.AccountId = a.AccountId
OUTER APPLY
(
    SELECT TOP (1) d.DiscountId, d.Name, d.[Percent]
    FROM dbo.MembershipDiscountTiers dt
    JOIN dbo.MembershipDiscounts d ON d.DiscountId = dt.DiscountId
    WHERE dt.TierCode = cm.TierCode AND d.IsActive = 1
      AND d.StartsAt <= @Now AND d.EndsAt > @Now
    ORDER BY d.[Percent] DESC, d.DiscountId
) offer
WHERE a.AccountId = @AccountId;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw AccessDenied();
        var role = reader.GetString(0);
        if (role == "Admin") return new BookingDiscountQuote(baseFare, MemberTier.Standard, null, null, 0, 0);
        if (role != "Customer" || reader.IsDBNull(1)) throw AccessDenied();
        var tier = ParseTier(reader.GetString(1));
        if (reader.IsDBNull(2)) return new BookingDiscountQuote(baseFare, tier, null, null, 0, 0);
        var percent = reader.GetDecimal(4);
        // Ticket fares, bank-transfer instructions and exports are all in whole VND.
        var discount = decimal.Round(baseFare * percent / 100m, 0, MidpointRounding.AwayFromZero);
        return new BookingDiscountQuote(baseFare, tier, reader.GetInt32(2), reader.GetString(3), percent, discount);
    }

    public static Task<IReadOnlyList<MemberNotification>> GetNotificationsAsync(int accountId) =>
        WithCustomerAsync<IReadOnlyList<MemberNotification>>(accountId, true, async (connection, transaction) =>
        {
            const string sql = @"
SELECT TOP (100) n.NotificationId, n.DiscountId, n.TierCode, d.Name, d.[Percent],
       d.StartsAt, d.EndsAt, n.CreatedAt, n.ReadAt, d.IsActive
FROM dbo.MembershipNotifications n
JOIN dbo.MembershipDiscounts d ON d.DiscountId = n.DiscountId
WHERE n.AccountId = @AccountId
ORDER BY n.CreatedAt DESC, n.NotificationId DESC;";
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            await using var reader = await command.ExecuteReaderAsync();
            var notifications = new List<MemberNotification>();
            while (await reader.ReadAsync())
                notifications.Add(new MemberNotification(reader.GetInt64(0), reader.GetInt32(1), ParseTier(reader.GetString(2)),
                    reader.GetString(3), reader.GetDecimal(4), Utc(reader.GetDateTime(5)), Utc(reader.GetDateTime(6)),
                    Utc(reader.GetDateTime(7)), reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8)), reader.GetBoolean(9)));
            return notifications;
        });

    public static Task<int> GetUnreadNotificationCountAsync(int accountId) =>
        WithCustomerAsync(accountId, true, (connection, transaction) => ReadUnreadCountAsync(connection, transaction, accountId));

    public static Task<bool> MarkNotificationReadAsync(int accountId, long notificationId) =>
        WithCustomerAsync(accountId, false, async (connection, transaction) =>
        {
            await using var command = new SqlCommand(@"
UPDATE dbo.MembershipNotifications SET ReadAt = COALESCE(ReadAt, SYSUTCDATETIME())
WHERE AccountId = @AccountId AND NotificationId = @NotificationId;", connection, transaction);
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            command.Parameters.Add("@NotificationId", SqlDbType.BigInt).Value = notificationId;
            return await command.ExecuteNonQueryAsync() == 1;
        });

    public static async Task MarkAllNotificationsReadAsync(int accountId) =>
        _ = await WithCustomerAsync(accountId, false, async (connection, transaction) =>
        {
            await using var command = new SqlCommand(@"
UPDATE dbo.MembershipNotifications SET ReadAt = SYSUTCDATETIME()
WHERE AccountId = @AccountId AND ReadAt IS NULL;", connection, transaction);
            command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
            return await command.ExecuteNonQueryAsync();
        });

    private static async Task<IReadOnlyList<MembershipTierInfo>> ReadTiersAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        await using var command = new SqlCommand(
            "SELECT TierCode, MinimumSpend FROM dbo.MembershipTiers ORDER BY MinimumSpend;", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var tiers = new List<MembershipTierInfo>();
        while (await reader.ReadAsync()) tiers.Add(new MembershipTierInfo(ParseTier(reader.GetString(0)), reader.GetDecimal(1)));
        return tiers;
    }

    private static async Task<IReadOnlyList<MemberDiscount>> ReadDiscountsAsync(SqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var discounts = new List<MemberDiscount>();
        List<MemberTier>? targets = null;
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            if (discounts.Count == 0 || discounts[^1].DiscountId != id)
            {
                targets = new List<MemberTier>();
                discounts.Add(new MemberDiscount(id, reader.GetString(1), reader.GetDecimal(2),
                    Utc(reader.GetDateTime(3)), Utc(reader.GetDateTime(4)), reader.GetBoolean(5), targets));
            }
            targets!.Add(ParseTier(reader.GetString(6)));
        }
        return discounts;
    }

    private static async Task<int> ReadUnreadCountAsync(SqlConnection connection, SqlTransaction transaction, int accountId)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.MembershipNotifications WHERE AccountId = @AccountId AND ReadAt IS NULL;", connection, transaction);
        command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task SyncNotificationsAsync(SqlConnection connection, SqlTransaction transaction, int accountId)
    {
        const string sql = @"
INSERT INTO dbo.MembershipNotifications (AccountId, DiscountId, TierCode)
SELECT cm.AccountId, d.DiscountId, cm.TierCode
FROM dbo.CustomerMemberships cm
JOIN dbo.MembershipDiscountTiers dt ON dt.TierCode = cm.TierCode
JOIN dbo.MembershipDiscounts d ON d.DiscountId = dt.DiscountId
WHERE cm.AccountId = @AccountId AND d.IsActive = 1 AND d.EndsAt > SYSUTCDATETIME()
  AND NOT EXISTS (SELECT 1 FROM dbo.MembershipNotifications n WITH (UPDLOCK, HOLDLOCK)
                  WHERE n.AccountId = cm.AccountId AND n.DiscountId = d.DiscountId);";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
        await command.ExecuteNonQueryAsync();
    }

    private static Task<T> WithCustomerAsync<T>(int accountId, bool syncNotifications,
        Func<SqlConnection, SqlTransaction, Task<T>> action)
    {
        RequireCustomer(accountId);
        return WithConnectionAsync("access customer membership", async connection =>
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            // Serialize sync/read flags for one account before taking notification key-range locks.
            await RequireDatabaseRoleAsync(connection, transaction, accountId, "Customer");
            if (syncNotifications) await SyncNotificationsAsync(connection, transaction, accountId);
            var result = await action(connection, transaction);
            await transaction.CommitAsync();
            return result;
        });
    }

    private static async Task<T> WithConnectionAsync<T>(string operation, Func<SqlConnection, Task<T>> action)
    {
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            return await action(connection);
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"Could not {operation}.", ex);
            throw new DataAccessException(ex);
        }
    }

    private static async Task RequireDatabaseRoleAsync(SqlConnection connection, SqlTransaction? transaction, int accountId, string role)
    {
        var sql = transaction is null
            ? "SELECT Role FROM dbo.Accounts WHERE AccountId = @AccountId;"
            : "SELECT Role FROM dbo.Accounts WITH (UPDLOCK, HOLDLOCK) WHERE AccountId = @AccountId;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId;
        if (await command.ExecuteScalarAsync() is not string storedRole || storedRole != role) throw AccessDenied();
    }

    private static void RequireCustomer(int accountId)
    {
        if (CurrentUser.Account is not { Role: "Customer" } account || account.AccountId != accountId) throw AccessDenied();
    }

    private static int RequireAdmin(int? adminId = null)
    {
        if (CurrentUser.Account is not { Role: "Admin" } account || (adminId.HasValue && adminId.Value != account.AccountId))
            throw AccessDenied();
        return account.AccountId;
    }

    private static UnauthorizedAccessException AccessDenied() =>
        new(T("Bạn không có quyền truy cập dữ liệu thành viên này.", "You do not have permission to access this membership data."));

    private static MemberTier ParseTier(string code) => Enum.Parse<MemberTier>(code, false);
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
