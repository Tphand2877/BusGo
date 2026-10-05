using System.IO;
using Serilog;

namespace BusGo.Services;

public static class LoggerService
{
    static LoggerService()
    {
        Directory.CreateDirectory(AppConfig.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppConfig.LogDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: AppConfig.RetainDays,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static void LogInfo(string message) => Log.Information(message);
    public static void LogWarning(string message) => Log.Warning(message);
    public static void LogError(string message, Exception? exception = null)
    {
        if (exception is null) Log.Error(message);
        else Log.Error(exception, message);
    }
}
