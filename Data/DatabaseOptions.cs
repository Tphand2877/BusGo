using BusGo.Services;
using Microsoft.Data.SqlClient;

namespace BusGo.Data;

public static class DatabaseOptions
{
    private const string UnknownDatabaseName = "(not configured)";

    public static string ConnectionString => AppConfig.ConnectionString;
    public static string MasterConnectionString => AppConfig.MasterConnectionString;

    /// <summary>
    /// Initial catalog of the configured connection string, for display on the
    /// admin screens. Returns a placeholder when the connection string is
    /// malformed or names no database.
    /// </summary>
    public static string DatabaseName
    {
        get
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(ConnectionString);
                return string.IsNullOrWhiteSpace(builder.InitialCatalog)
                    ? UnknownDatabaseName
                    : builder.InitialCatalog;
            }
            catch (ArgumentException ex)
            {
                LoggerService.LogWarning($"Connection string could not be parsed for the database name: {ex.Message}");
                return UnknownDatabaseName;
            }
        }
    }
}
