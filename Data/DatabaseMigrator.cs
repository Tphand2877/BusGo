using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BusGo.Services;
using Microsoft.Data.SqlClient;

namespace BusGo.Data;

public static class DatabaseMigrator
{
    private const string HistoryTable = "__DatabaseMigrations";

    /// <summary>Below this many days of future departures the schedule is topped up.</summary>
    private const int MinimumScheduleDaysAhead = 2;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? LastError { get; private set; }
    public static bool IsReady { get; internal set; }
    public static string StatusMessage { get; internal set; } = "Initializing...";
    public static int AttemptCount { get; internal set; }

    public static string GetStartupHtml()
    {
        var rawConn = DatabaseOptions.ConnectionString;
        var maskedConn = Regex.Replace(rawConn, @"(?i)(Password|Pwd)\s*=\s*[^;]+", "$1=******");
        return $$"""
<!DOCTYPE html>
<html lang="vi">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta http-equiv="refresh" content="3" />
    <title>BusGo - Đang kết nối</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0b1320; color: #f1f5f9; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 1.5rem; box-sizing: border-box; }
        .card { background: #132238; border: 1px solid #1e3a5f; border-radius: 16px; padding: 2rem; max-width: 520px; width: 100%; box-shadow: 0 20px 40px rgba(0,0,0,0.5); text-align: center; }
        .logo { font-size: 2.2rem; font-weight: 800; color: #38bdf8; margin-bottom: 0.5rem; }
        .spinner { width: 44px; height: 44px; border: 4px solid #1e3a5f; border-top-color: #38bdf8; border-radius: 50%; animation: spin 1s linear infinite; margin: 1.5rem auto; }
        @keyframes spin { to { transform: rotate(360deg); } }
        h1 { font-size: 1.25rem; font-weight: 600; margin: 0 0 0.5rem; }
        p { color: #94a3b8; font-size: 0.9rem; line-height: 1.5; margin: 0.5rem 0; }
        .detail { background: #0a101d; border-radius: 8px; padding: 0.75rem 1rem; font-family: monospace; font-size: 0.8rem; color: #cbd5e1; word-break: break-all; margin: 1rem 0; text-align: left; }
        .badge { display: inline-block; background: #0369a1; color: #e0f2fe; padding: 0.25rem 0.75rem; border-radius: 9999px; font-size: 0.75rem; font-weight: 600; margin-bottom: 1rem; }
    </style>
</head>
<body>
    <div class="card">
        <div class="logo">🚌 BusGo</div>
        <div class="badge">Đang khởi động hệ thống</div>
        <div class="spinner"></div>
        <h1>Đang kết nối cơ sở dữ liệu SQL Server</h1>
        <p>Hệ thống tự động kết nối và nạp bảng dữ liệu. Trang sẽ tự động chuyển tiếp khi hoàn tất.</p>
        <div class="detail">
            <strong>Trạng thái:</strong> {{System.Net.WebUtility.HtmlEncode(StatusMessage)}}<br/>
            <strong>Lần thử:</strong> {{AttemptCount}}<br/>
            <strong>Cấu hình DB:</strong> {{System.Net.WebUtility.HtmlEncode(maskedConn)}}
        </div>
        <p style="font-size:0.8rem; color:#64748b;">Trên Railway, hãy đảm bảo bạn đã tạo service SQL Server và cấu hình biến <code>BUS_TICKET_CONNECTION_STRING</code>.</p>
    </div>
</body>
</html>
""";
    }

    public static async Task<bool> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            LastError = null;
            await EnsureDatabaseCreatedAsync(cancellationToken);
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureHistoryTableAsync(connection, cancellationToken);

            await ApplyMigrationAsync(connection, "001_InitialSchema", "Schema1.sql", skipWhenAccountsExist: true, cancellationToken);
            await ApplyMigrationAsync(connection, "002_TicketFields", "Migration_001.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "003_AdminRole", "Migration_002_Admin.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "004_Payments", "Schema_Payments.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "005_PerformanceIndexes", "Migration_003_PerformanceIndexes.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "006_PaymentExpiry", "Migration_004_PaymentExpiry.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "007_DevelopmentSeedData", "SeedData.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "008_SettlementHardening", "Migration_005_Settlement.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "009_MultiSeatTicket", "Migration_006_MultiSeatTicket.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "010_BusCompanyAndRegistration", "Migration_007_BusCompanyAndRegistration.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "011_SeatUniqueness", "Migration_008_SeatUniqueness.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "012_TriggerRollbackFix", "Migration_009_TriggerRollbackFix.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "013_LoginThrottling", "Migration_010_LoginThrottling.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "014_PaymentIntegrity", "Migration_011_PaymentIntegrity.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "015_AuditLog", "Migration_012_AuditLog.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "016_SeatAvailabilityIndexes", "Migration_013_SeatAvailabilityIndexes.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "017_StagePrice", "Migration_014_StagePrice.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "018_StationOperationsAndCommission", "Migration_015_StationOperationsAndCommission.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "019_MembershipDiscounts", "Migration_016_MembershipDiscounts.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "020_OwnerRoleAndAuditLogs", "Migration_017_AuditLogs.sql", skipWhenAccountsExist: false, cancellationToken);
            await ApplyMigrationAsync(connection, "021_CheckInAttribution", "Migration_018_CheckInAttribution.sql", skipWhenAccountsExist: false, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            LoggerService.LogError("Database migration failed.", ex);
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Keeps the departure schedule populated. Runs <c>Data/DevSeedTrips.sql</c>
    /// when the database has fewer than <see cref="MinimumScheduleDaysAhead"/>
    /// days of future departures left, which tops the schedule up to 14 days
    /// ahead. Safe to call on every startup and never throws.
    /// </summary>
    /// <returns>The number of trips added; zero when nothing was needed.</returns>
    public static async Task<int> EnsureUpcomingTripsAsync(CancellationToken cancellationToken = default)
    {
        const string horizonSql =
            "SELECT COALESCE(DATEDIFF(DAY, SYSUTCDATETIME(), MAX(DepartureTime)), -1) FROM dbo.Trips WHERE DepartureTime >= SYSUTCDATETIME();";
        const string countSql = "SELECT COUNT(*) FROM dbo.Trips WHERE DepartureTime >= SYSUTCDATETIME();";

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);

            await DatabaseHelper.EnsureBusCompaniesSyncedAsync();

            var daysAhead = await ExecuteIntAsync(connection, horizonSql, cancellationToken);
            if (daysAhead >= MinimumScheduleDaysAhead)
                return 0;

            var before = await ExecuteIntAsync(connection, countSql, cancellationToken);
            var script = await LoadScriptAsync("DevSeedTrips.sql", cancellationToken);
            await ExecuteBatchesAsync(connection, transaction: null, script, "DevSeedTrips.sql", cancellationToken);
            var added = await ExecuteIntAsync(connection, countSql, cancellationToken) - before;

            LoggerService.LogInfo(
                $"Schedule top-up: only {daysAhead} day(s) of departures remained, added {added} trip(s) for the next two weeks.");
            return added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerService.LogError("Could not top up the upcoming trip schedule.", ex);
            return 0;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Seeds one default Owner account on startup if no Owner exists.
    /// Reads configuration from Security:DefaultOwner with clearly-marked dev defaults.
    /// </summary>
    public static async Task<bool> EnsureDefaultOwnerAsync(IConfiguration? configuration = null, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);

            const string checkSql = "SELECT COUNT(*) FROM dbo.Accounts WHERE Role = N'Owner';";
            var ownerCount = await ExecuteIntAsync(connection, checkSql, cancellationToken);
            if (ownerCount > 0)
                return false;

            var username = configuration?["Security:DefaultOwner:Username"] ?? "owner";
            var email = configuration?["Security:DefaultOwner:Email"] ?? "owner@busgo.local";
            var fullName = configuration?["Security:DefaultOwner:FullName"] ?? "Station Owner";
            var phone = configuration?["Security:DefaultOwner:Phone"] ?? "0988888888";
            var password = configuration?["Security:DefaultOwner:Password"] ?? "Owner!234";

            const string existingUserSql = "SELECT AccountId FROM dbo.Accounts WHERE Username = @Username;";
            await using (var checkCmd = new SqlCommand(existingUserSql, connection))
            {
                checkCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
                var existingId = await checkCmd.ExecuteScalarAsync(cancellationToken);
                if (existingId is int id)
                {
                    const string promoteSql = "UPDATE dbo.Accounts SET Role = N'Owner', PasswordHash = @Hash WHERE AccountId = @Id;";
                    await using var promoteCmd = new SqlCommand(promoteSql, connection);
                    promoteCmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
                    promoteCmd.Parameters.Add("@Hash", SqlDbType.NVarChar, 255).Value = DatabaseHelper.HashPassword(password);
                    await promoteCmd.ExecuteNonQueryAsync(cancellationToken);
                    LoggerService.LogInfo($"Existing user '{username}' promoted to default Owner.");
                    return true;
                }
            }

            const string insertSql = @"
INSERT INTO dbo.Accounts (Username, PasswordHash, FullName, Phone, Email, Role)
VALUES (@Username, @Hash, @FullName, @Phone, @Email, N'Owner');";
            await using var insertCmd = new SqlCommand(insertSql, connection);
            insertCmd.Parameters.Add("@Username", SqlDbType.NVarChar, 50).Value = username;
            insertCmd.Parameters.Add("@Hash", SqlDbType.NVarChar, 255).Value = DatabaseHelper.HashPassword(password);
            insertCmd.Parameters.Add("@FullName", SqlDbType.NVarChar, 100).Value = fullName;
            insertCmd.Parameters.Add("@Phone", SqlDbType.NVarChar, 20).Value = (object?)phone ?? DBNull.Value;
            insertCmd.Parameters.Add("@Email", SqlDbType.NVarChar, 100).Value = (object?)email ?? DBNull.Value;
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            LoggerService.LogInfo($"Default Owner account '{username}' created successfully.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerService.LogError("Could not ensure default Owner account.", ex);
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task EnsureDatabaseCreatedAsync(CancellationToken cancellationToken)
    {
        var appBuilder = new SqlConnectionStringBuilder(DatabaseOptions.ConnectionString);
        if (string.IsNullOrWhiteSpace(appBuilder.InitialCatalog))
            throw new InvalidOperationException("Connection string must specify a database name.");

        try
        {
            await using var targetConn = DatabaseHelper.GetConnection();
            await targetConn.OpenAsync(cancellationToken);
            return;
        }
        catch (SqlException)
        {
            // Target database may not exist yet; attempt creation via master connection.
        }

        var databaseName = appBuilder.InitialCatalog;
        await using var connection = new SqlConnection(DatabaseOptions.MasterConnectionString);
        await connection.OpenAsync(cancellationToken);
        var escapedName = databaseName.Replace("]", "]]", StringComparison.Ordinal);
        var sql = $"IF DB_ID(N'{databaseName.Replace("'", "''", StringComparison.Ordinal)}') IS NULL CREATE DATABASE [{escapedName}];";
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureHistoryTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.__DatabaseMigrations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.__DatabaseMigrations
    (
        MigrationId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DatabaseMigrations PRIMARY KEY,
        ScriptName NVARCHAR(255) NOT NULL CONSTRAINT UQ_DatabaseMigrations_ScriptName UNIQUE,
        AppliedAt DATETIME2 NOT NULL CONSTRAINT DF_DatabaseMigrations_AppliedAt DEFAULT SYSUTCDATETIME()
    );
END;
/* Checksum of the script as applied. Editing an already-applied migration used to be
   silently ignored on existing databases, so two installations could claim the same
   history with different schemas (AUD-B-009). NULL means 'recorded before checksums
   existed' and is accepted without complaint. */
IF COL_LENGTH(N'dbo.__DatabaseMigrations', N'Checksum') IS NULL
    ALTER TABLE dbo.__DatabaseMigrations ADD Checksum CHAR(64) NULL;";
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>SHA-256 of the normalised script, as stored in the history table.</summary>
    private static string ComputeChecksum(string script) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

    /// <summary>
    /// Fails loudly when an already-applied script has been edited since it ran.
    /// </summary>
    private static async Task VerifyChecksumAsync(SqlConnection connection, string name, string fileName, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"SELECT Checksum FROM dbo.{HistoryTable} WHERE ScriptName = @ScriptName;", connection);
        command.Parameters.Add("@ScriptName", SqlDbType.NVarChar, 255).Value = name;
        if (await command.ExecuteScalarAsync(cancellationToken) is not string recorded) return;

        var current = ComputeChecksum(await LoadScriptAsync(fileName, cancellationToken));
        if (string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase)) return;

        throw new InvalidOperationException(
            $"Migration '{name}' ({fileName}) has changed since it was applied to this database. " +
            "Applied migrations are immutable: add a new migration instead of editing this one.");
    }

    private static async Task ApplyMigrationAsync(SqlConnection connection, string name, string fileName, bool skipWhenAccountsExist, CancellationToken cancellationToken)
    {
        if (await IsAppliedAsync(connection, name, cancellationToken))
        {
            await VerifyChecksumAsync(connection, name, fileName, cancellationToken);
            return;
        }

        if (skipWhenAccountsExist && await TableExistsAsync(connection, "Accounts", cancellationToken))
        {
            await RecordMigrationAsync(connection, name, cancellationToken);
            LoggerService.LogWarning(
                $"Migration '{name}' ({fileName}) was recorded as applied WITHOUT executing because table dbo.Accounts already exists. " +
                $"The objects it would have created are therefore absent from this database: the CK_Tickets_Status / CK_Tickets_PaymentStatus " +
                $"check constraints, the UX_Tickets_Active_Trip_Seat anti-double-booking unique index and the TR_Tickets_ValidateSeatBus " +
                $"seat/bus trigger. Migration '008_SettlementHardening' ({nameof(DatabaseMigrator)} step 8) creates all of them idempotently.");
            return;
        }

        var script = await LoadScriptAsync(fileName, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            // The script and its history row commit together: a crash can never
            // record a migration that did not run, nor run one it forgot.
            await ExecuteBatchesAsync(connection, transaction, script, fileName, cancellationToken);

            await using var record = new SqlCommand(
                $"INSERT INTO dbo.{HistoryTable} (ScriptName, Checksum) VALUES (@ScriptName, @Checksum);", connection, transaction);
            record.Parameters.Add("@ScriptName", SqlDbType.NVarChar, 255).Value = name;
            record.Parameters.Add("@Checksum", SqlDbType.Char, 64).Value = ComputeChecksum(script);
            await record.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LoggerService.LogInfo($"Applied migration '{name}' ({fileName}).");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> LoadScriptAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Migration script not found: {path}");
        return NormalizeScript(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static async Task ExecuteBatchesAsync(SqlConnection connection, SqlTransaction? transaction, string script, string fileName, CancellationToken cancellationToken)
    {
        var batchIndex = 0;
        foreach (var batch in SplitBatches(script))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            batchIndex++;
            try
            {
                await using var command = transaction is null
                    ? new SqlCommand(batch, connection)
                    : new SqlCommand(batch, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoggerService.LogError($"Script '{fileName}' failed in batch {batchIndex}.", ex);
                throw;
            }
        }
    }

    private static async Task<int> ExecuteIntAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> IsAppliedAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT COUNT(1) FROM dbo.{HistoryTable} WHERE ScriptName=@ScriptName;", connection);
        command.Parameters.Add("@ScriptName", SqlDbType.NVarChar, 255).Value = name;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@TableName, N'U') IS NULL THEN 0 ELSE 1 END;", connection);
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 256).Value = $"dbo.{tableName}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task RecordMigrationAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"INSERT INTO dbo.{HistoryTable} (ScriptName) VALUES (@ScriptName);", connection);
        command.Parameters.Add("@ScriptName", SqlDbType.NVarChar, 255).Value = name;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeScript(string script)
    {
        script = Regex.Replace(script, @"^\s*USE\s+[^;\r\n]+;\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return Regex.Replace(script, @"^\s*IF\s+DB_ID\([^)]+\)\s+IS\s+NULL\s+CREATE\s+DATABASE\s+[^;]+;\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }
    private static string[] SplitBatches(string script) =>
        Regex.Split(script, @"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
}
