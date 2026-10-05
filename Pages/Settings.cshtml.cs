using System.ComponentModel.DataAnnotations;
using BusGo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static BusGo.Services.UiText;

namespace BusGo.Pages;

[AllowAnonymous]
public sealed class SettingsModel : PageModel
{
    [BindProperty, Display(Name = nameof(SelectedLanguage)), Required(ErrorMessage = "The {0} field is required.")] public string SelectedLanguage { get; set; } = "vi-VN";

    public string SelectedAppearance => UiAppearance.Read(Request);

    public void OnGet() => SelectedLanguage = IsEnglish ? "en-US" : "vi-VN";

    public IActionResult OnPostAppearance(string? appearance)
    {
        if (!UiAppearance.IsValid(appearance))
        {
            TempData["Error"] = T("Chọn giao diện Sáng, Tối hoặc Theo hệ thống.", "Choose Light, Dark or System appearance.");
            return RedirectToPage();
        }

        Response.Cookies.Append(UiAppearance.CookieName, appearance!, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            HttpOnly = false,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/"
        });
        TempData["Success"] = T("Đã lưu giao diện cho trình duyệt này.", "Appearance saved for this browser.");
        return RedirectToPage();
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid) return Page();
        if (SelectedLanguage is not ("vi-VN" or "en-US"))
        {
            ModelState.AddModelError(nameof(SelectedLanguage), T("Chọn Tiếng Việt hoặc English.", "Choose Vietnamese or English."));
            return Page();
        }

        Response.Cookies.Append("BusGo.Language",
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(SelectedLanguage)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/"
            });
        TempData["Success"] = SelectedLanguage == "en-US"
            ? "Language saved. BusGo now uses English."
            : "Đã lưu ngôn ngữ. BusGo hiện dùng Tiếng Việt.";
        return RedirectToPage();
    }
}
