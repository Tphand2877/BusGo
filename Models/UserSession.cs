namespace BusGo.Models;

public sealed record UserSession(
    int AccountId,
    string Username,
    string FullName,
    string? Email,
    string? Phone,
    string Role);

public static class CurrentUser
{
    private static readonly AsyncLocal<UserSession?> Session = new();

    public static UserSession? Account
    {
        get => Session.Value;
        set => Session.Value = value;
    }
    public static bool IsOwner => string.Equals(Account?.Role, "Owner", StringComparison.OrdinalIgnoreCase);
    public static bool IsAdmin => IsOwner || string.Equals(Account?.Role, "Admin", StringComparison.OrdinalIgnoreCase);
}
