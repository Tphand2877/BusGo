using BusGo.Services;
using Microsoft.Data.SqlClient;

namespace BusGo.Data;

/// <summary>
/// Retries a self-contained database operation after a transient server fault
/// (AUD-B-006).
/// </summary>
/// <remarks>
/// Serializable transactions holding range locks over the seat rows are exactly the
/// workload that produces deadlocks, and the anti-double-sell index added in migration
/// 008 makes contention surface as error 1205 more often than before. Without a retry a
/// lost race reaches the customer as a generic failure even though nothing is wrong.
/// <para>
/// Only operations that fully roll back on failure may be retried: every caller here
/// either commits or rolls back its whole unit of work, so a retry cannot double-apply.
/// </para>
/// </remarks>
internal static class SqlResilience
{
    private const int MaxAttempts = 3;

    /// <summary>Base backoff; attempt N waits <c>N * BaseDelay</c> plus jitter.</summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Server-side faults that are worth retrying: deadlock victim, command timeout,
    /// connection/login pressure, and the Azure SQL throttling family.
    /// </summary>
    internal static bool IsTransient(SqlException exception) => exception.Number is
        1205 or      // deadlock victim
        -2 or        // command timeout
        4060 or      // cannot open database (transient during failover)
        40197 or 40501 or 40613 or
        49918 or 49919 or 49920;

    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string description)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqlException ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                var delay = BaseDelay * attempt + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 80));
                LoggerService.LogWarning(
                    $"Transient SQL error {ex.Number} while trying to {description}; retrying in {delay.TotalMilliseconds:F0} ms (attempt {attempt} of {MaxAttempts}).");
                await Task.Delay(delay);
            }
        }
    }
}
