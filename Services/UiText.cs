using System.Globalization;

namespace BusGo.Services;

/// <summary>Two-language UI copy, evaluated in the current request culture.</summary>
public static class UiText
{
    public static bool IsEnglish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";
    public static string Language => IsEnglish ? "en" : "vi";
    public static string T(string vietnamese, string english) => IsEnglish ? english : vietnamese;
    public static string Format(string vietnamese, string english, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(vietnamese, english), arguments);
    public static string Value(string? value) => DomainText.ToDisplay(value);
}
