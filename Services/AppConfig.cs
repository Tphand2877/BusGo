using System.IO;
using Microsoft.Extensions.Configuration;

namespace BusGo.Services;

public static class AppConfig
{
    private static readonly IConfigurationRoot Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .Build();

    public static string ConnectionString => CleanConnectionString(
        Environment.GetEnvironmentVariable("BUS_TICKET_CONNECTION_STRING")
        ?? Configuration.GetConnectionString("DefaultConnection")
        ?? "Server=localhost;Database=BusTicketSaleSystem;Integrated Security=True;TrustServerCertificate=True;Encrypt=False;");

    public static string MasterConnectionString => CleanConnectionString(
        Environment.GetEnvironmentVariable("BUS_TICKET_MASTER_CONNECTION_STRING")
        ?? Configuration.GetConnectionString("MasterConnection")
        ?? DeriveMasterConnectionString(ConnectionString));

    private static string CleanConnectionString(string? connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return string.Empty;
        connStr = System.Text.RegularExpressions.Regex.Replace(connStr, @"[\u00A0\u1680\u2000-\u200B\u202F\u205F\u3000]", " ");
        connStr = System.Text.RegularExpressions.Regex.Replace(connStr, @"(?i)\buser\s*id\b", "UID");
        connStr = System.Text.RegularExpressions.Regex.Replace(connStr, @"(?i)\bpassword\b", "PWD");
        return connStr.Trim().Trim('"', '\'');
    }

    private static string DeriveMasterConnectionString(string appConnStr)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(appConnStr)
            {
                InitialCatalog = "master"
            };
            return builder.ConnectionString;
        }
        catch
        {
            return "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;Encrypt=False;";
        }
    }
    public static string LogDirectory =>
        Configuration["Logging:LogDirectory"] is { Length: > 0 } value
            ? Path.Combine(AppContext.BaseDirectory, value)
            : Path.Combine(AppContext.BaseDirectory, "Logs");

    public static int RetainDays =>
        int.TryParse(Configuration["Logging:RetainDays"], out var days) && days > 0 ? days : 30;
    public static string VnPayTmnCode =>
        Environment.GetEnvironmentVariable("VNPAY_TMN_CODE") ?? string.Empty;

    public static string VnPayHashSecret =>
        Environment.GetEnvironmentVariable("VNPAY_HASH_SECRET") ?? string.Empty;

    /// <summary>Legacy local callback port; hosted deployments must set VNPAY_RETURN_URL.</summary>
    public static int VnPayCallbackPort =>
        int.TryParse(Environment.GetEnvironmentVariable("VNPAY_CALLBACK_PORT"), out var port) && port is > 0 and <= 65535
            ? port
            : 5058;

    public static string VnPayCallbackPrefix => $"http://127.0.0.1:{VnPayCallbackPort}/vnpay-return/";

    public static string VnPayReturnUrl =>
        Environment.GetEnvironmentVariable("VNPAY_RETURN_URL") ?? VnPayCallbackPrefix;

    public static string BrevoApiKey =>
        Environment.GetEnvironmentVariable("BREVO_API_KEY") ?? string.Empty;

    public static string EmailSenderName =>
        Environment.GetEnvironmentVariable("EMAIL_SENDER_NAME") ?? "BusGo";
    public static string EmailSenderAddress =>
        Environment.GetEnvironmentVariable("EMAIL_SENDER_ADDRESS") ?? string.Empty;
}
