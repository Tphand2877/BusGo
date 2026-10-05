using static BusGo.Services.UiText;

namespace BusGo.Data;

/// <summary>A safe public error that retains database diagnostics only in its inner exception.</summary>
public sealed class DataAccessException(Exception innerException)
    : Exception(T("Cơ sở dữ liệu không khả dụng. Vui lòng thử lại hoặc liên hệ quản trị viên.", "The database is unavailable. Please try again or contact the administrator."), innerException)
{
}
