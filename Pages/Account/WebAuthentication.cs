using System.Security.Claims;
using BusGo.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace BusGo.Pages.Account;

internal static class WebAuthentication
{
    internal static Task SignInAsync(HttpContext context, UserSession account, AuthenticationProperties? properties = null)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(ClaimTypes.GivenName, account.FullName),
            new Claim(ClaimTypes.Email, account.Email ?? string.Empty),
            new Claim(ClaimTypes.MobilePhone, account.Phone ?? string.Empty),
            new Claim(ClaimTypes.Role, account.Role)
        };
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), properties);
    }
}
