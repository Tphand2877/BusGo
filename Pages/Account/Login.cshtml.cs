using System.ComponentModel.DataAnnotations;
using BusGo.Data;
using BusGo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using static BusGo.Services.UiText;
namespace BusGo.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    [BindProperty, Display(Name = nameof(Username)), Required(ErrorMessage = "The {0} field is required.")] public string Username { get; set; } = string.Empty;
    [BindProperty, Display(Name = nameof(Password)), Required(ErrorMessage = "The {0} field is required."), DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true
        ? RedirectToPage(User.IsInRole("Admin") ? "/Admin/Index" : "/Index")
        : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            var result = await DatabaseHelper.AuthenticateAsync(Username.Trim(), Password);
            if (!result.Success || result.Account is null)
            {
                ModelState.AddModelError(string.Empty, result.Error ?? T("Đăng nhập không thành công.", "Sign-in failed."));
                return Page();
            }
            await WebAuthentication.SignInAsync(HttpContext, result.Account);
            if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)) return LocalRedirect(ReturnUrl);
            return RedirectToPage(string.Equals(result.Account.Role, "Admin", StringComparison.OrdinalIgnoreCase) ? "/Admin/Index" : "/Index");
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Web sign-in failed.", ex);
            ModelState.AddModelError(string.Empty, T("Không thể đăng nhập. Vui lòng kiểm tra kết nối cơ sở dữ liệu hoặc thử lại sau.", "Sign-in is unavailable. Please check the database connection or try again later."));
            return Page();
        }
    }
}
