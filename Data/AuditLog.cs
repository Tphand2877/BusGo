using System.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.Data.SqlClient;

namespace BusGo.Data;

/// <summary>
/// Durable record of privileged operations (AUD-D-009).
/// </summary>
/// <remarks>
/// Writes go to <c>dbo.AuditLog</c>. Where an enclosing transaction is supplied the
/// audit row commits or rolls back with the operation it describes, so the trail can
/// never claim something that did not happen. Auditing must never be the reason an
/// operation fails, so a logging failure is recorded to the file log and swallowed —
/// except when it is part of the caller's transaction, where the caller already owns
/// the failure.
/// </remarks>
internal static class AuditLog
{
    private const string InsertSql = @"
INSERT INTO dbo.AuditLog (ActorAccountId, Action, TargetType, TargetId, Detail)
VALUES (@Actor, @Action, @TargetType, @TargetId, @Detail);";

    /// <summary>Records an action on its own connection.</summary>
    public static async Task WriteAsync(string action, string targetType, string? targetId, string? detail = null)
    {
        try
        {
            await using var connection = DatabaseHelper.GetConnection();
            await connection.OpenAsync();
            await using var command = Build(connection, null, action, targetType, targetId, detail);
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            LoggerService.LogError($"The audit entry for '{action}' could not be written.", ex);
        }
    }

    /// <summary>Records an action inside the caller's transaction.</summary>
    public static async Task WriteAsync(
        SqlConnection connection, SqlTransaction? transaction,
        string action, string targetType, string? targetId, string? detail = null)
    {
        await using var command = Build(connection, transaction, action, targetType, targetId, detail);
        await command.ExecuteNonQueryAsync();
    }

    private static SqlCommand Build(
        SqlConnection connection, SqlTransaction? transaction,
        string action, string targetType, string? targetId, string? detail)
    {
        var command = transaction is null
            ? new SqlCommand(InsertSql, connection)
            : new SqlCommand(InsertSql, connection, transaction);

        command.Parameters.Add("@Actor", SqlDbType.Int).Value =
            CurrentUser.Account is { } account ? account.AccountId : DBNull.Value;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 60).Value = action;
        command.Parameters.Add("@TargetType", SqlDbType.NVarChar, 40).Value = targetType;
        command.Parameters.Add("@TargetId", SqlDbType.NVarChar, 60).Value = (object?)targetId ?? DBNull.Value;
        command.Parameters.Add("@Detail", SqlDbType.NVarChar, 400).Value = (object?)Truncate(detail) ?? DBNull.Value;
        return command;
    }

    private static string? Truncate(string? detail) =>
        detail is { Length: > 400 } ? detail[..400] : detail;
}
