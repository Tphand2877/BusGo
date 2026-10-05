namespace BusGo.Services;

public static class UiAppearance
{
    public const string CookieName = "BusGo.Appearance";

    public static bool IsValid(string? value) => value is "light" or "dark" or "system";

    public static string Read(HttpRequest request)
    {
        var value = request.Cookies[CookieName];
        return IsValid(value) ? value! : "system";
    }
}
