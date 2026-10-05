using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static BusGo.Services.UiText;

namespace BusGo.Pages.Account;

[Authorize]
public sealed class ProfileModel : PageModel
{
    [BindProperty] public string FullName { get; set; } = string.Empty;
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public string? CurrentPassword { get; set; }
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }

    public void OnGet()
    {
        FullName = CurrentUser.Account!.FullName;
        Email = CurrentUser.Account.Email;
        Phone = CurrentUser.Account.Phone;
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        if (CurrentUser.Account is not { } account) return Challenge();
        try
        {
            var result = await DatabaseHelper.UpdateProfileAsync(account, FullName?.Trim() ?? string.Empty, Email?.Trim(), Phone?.Trim());
            if (!result.Success || result.Account is null)
            {
                TempData["Error"] = result.Error ?? T("Không thể lưu hồ sơ.", "Your profile could not be saved.");
            }
            else
            {
                var authentication = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                await WebAuthentication.SignInAsync(HttpContext, result.Account, authentication.Properties);
                CurrentUser.Account = result.Account;
                TempData["Success"] = T("Đã cập nhật hồ sơ.", "Your profile has been updated.");
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Updating web profile failed.", ex);
            TempData["Error"] = T("Không thể lưu hồ sơ. Vui lòng thử lại sau.", "Your profile could not be saved. Please try again later.");
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (CurrentUser.Account is not { } account) return Challenge();
        if (string.IsNullOrWhiteSpace(CurrentPassword) || string.IsNullOrWhiteSpace(NewPassword) || NewPassword != ConfirmPassword)
        {
            TempData["Error"] = T("Điền đầy đủ các mục mật khẩu và kiểm tra xác nhận mật khẩu khớp nhau.", "Fill in all password fields and make sure the confirmation matches.");
            return RedirectToPage();
        }
        try
        {
            var result = await DatabaseHelper.ChangePasswordAsync(account.AccountId, CurrentPassword, NewPassword);
            TempData[result.Success ? "Success" : "Error"] = result.Success ? T("Đã đổi mật khẩu.", "Your password has been changed.") : result.Error ?? T("Không thể đổi mật khẩu.", "Your password could not be changed.");
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Updating web password failed.", ex);
            TempData["Error"] = T("Không thể đổi mật khẩu. Vui lòng thử lại sau.", "Your password could not be changed. Please try again later.");
        }
        return RedirectToPage();
    }
}
