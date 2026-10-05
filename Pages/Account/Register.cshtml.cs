using System.ComponentModel.DataAnnotations;
using BusGo.Data;
using BusGo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using static BusGo.Services.UiText;
namespace BusGo.Pages.Account;

[AllowAnonymous]
public sealed class RegisterModel : PageModel
{
    [BindProperty, Display(Name = nameof(FullName)), Required(ErrorMessage = "The {0} field is required."), StringLength(100, ErrorMessage = "The field {0} must be a string with a maximum length of {1}.")] public string FullName { get; set; } = string.Empty;
    [BindProperty, Display(Name = nameof(Username)), Required(ErrorMessage = "The {0} field is required."), StringLength(50, ErrorMessage = "The field {0} must be a string with a maximum length of {1}."), RegularExpression(@"^\S+$", ErrorMessage = "Username cannot contain spaces.")] public string Username { get; set; } = string.Empty;
    [BindProperty, Display(Name = nameof(Email)), EmailAddress(ErrorMessage = "The {0} field is not a valid e-mail address.")] public string? Email { get; set; }
    [BindProperty, Display(Name = nameof(Phone)), RegularExpression(@"^\+?[0-9]{8,15}$", ErrorMessage = "Phone must contain 8 to 15 digits with an optional leading +.")] public string? Phone { get; set; }
    [BindProperty, Display(Name = nameof(Password)), Required(ErrorMessage = "The {0} field is required."), DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [BindProperty, Display(Name = nameof(ConfirmPassword)), Required(ErrorMessage = "The {0} field is required."), DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "'{0}' and '{1}' do not match.")] public string ConfirmPassword { get; set; } = string.Empty;

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            var result = await DatabaseHelper.CreateAccountAsync(Username.Trim(), Password, FullName.Trim(), Phone?.Trim(), Email?.Trim());
            if (!result.Success)
            {
                ModelState.AddModelError(string.Empty, result.Error ?? T("Không thể tạo tài khoản.", "The account could not be created."));
                return Page();
            }
            TempData["Success"] = T("Đã tạo tài khoản. Bạn có thể đăng nhập ngay.", "Your account has been created. You can sign in now.");
            return RedirectToPage("Login");
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Web registration failed.", ex);
            ModelState.AddModelError(string.Empty, T("Không thể đăng ký. Vui lòng thử lại sau.", "Registration is unavailable. Please try again later."));
            return Page();
        }
    }
}
