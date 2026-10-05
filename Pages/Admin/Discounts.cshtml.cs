using static BusGo.Services.UiText;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Mvc;

namespace BusGo.Pages.Admin;

public sealed class DiscountsModel : AdminPageModel
{
    public static IReadOnlyList<MemberTier> RewardTiers { get; } = [MemberTier.Bronze, MemberTier.Silver, MemberTier.Gold, MemberTier.Diamond];
    public IReadOnlyList<MembershipTierInfo> Tiers { get; private set; } = [];
    public IReadOnlyList<MemberDiscount> Discounts { get; private set; } = [];
    public DateTime NowUtc { get; } = DateTime.UtcNow;
    [BindProperty] public DiscountForm Input { get; set; } = new();

    public sealed class DiscountForm
    {
        [Display(Name = "DiscountName")] public string? Name { get; set; }
        [Display(Name = "DiscountPercent")] public string? Percent { get; set; }
        [Display(Name = "DiscountStartsAt")] public string? StartsAt { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        [Display(Name = "DiscountEndsAt")] public string? EndsAt { get; set; } = DateTime.Now.AddDays(7).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        [Display(Name = "DiscountTargetTiers")] public string[] TargetTiers { get; set; } = [];
    }

    private Task LoadAsync() => LoadSafely(async () =>
    {
        Tiers = await MembershipDatabaseService.GetTierDefinitionsAsync();
        Discounts = await MembershipDatabaseService.GetDiscountsAsync();
    });

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    private bool ValidateInput(out MembershipDiscountInput campaign)
    {
        var name = Input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100)
            ModelState.AddModelError("Input.Name", T("Tên ưu đãi phải có từ 1 đến 100 ký tự.", "Offer name must contain 1 to 100 characters."));
        var validPercent = decimal.TryParse(Input.Percent, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent);
        if (!validPercent || percent < 0.01m || percent > 99m || decimal.Round(percent, 2) != percent)
            ModelState.AddModelError("Input.Percent", T("Mức giảm phải từ 0,01 đến 99%, tối đa hai chữ số thập phân.", "Discount must be between 0.01 and 99%, with at most two decimal places."));
        var validStart = TryLocalUtc(Input.StartsAt, out var start);
        var validEnd = TryLocalUtc(Input.EndsAt, out var end);
        if (!validStart)
            ModelState.AddModelError("Input.StartsAt", T("Vui lòng nhập thời điểm bắt đầu hợp lệ theo giờ địa phương của máy chủ.", "Enter a valid start date and time in the server's local time zone."));
        if (!validEnd)
            ModelState.AddModelError("Input.EndsAt", T("Vui lòng nhập thời điểm kết thúc hợp lệ theo giờ địa phương của máy chủ.", "Enter a valid end date and time in the server's local time zone."));
        if (validStart && validEnd && (end <= start || end <= DateTime.UtcNow))
            ModelState.AddModelError("Input.EndsAt", T("Thời điểm kết thúc phải sau thời điểm bắt đầu và sau thời điểm hiện tại.", "End time must be after the start time and the current time."));
        var selected = Input.TargetTiers ?? [];
        var tiers = new List<MemberTier>();
        foreach (var code in selected)
        {
            if (!RewardTiers.Any(t => t.ToString() == code))
                ModelState.AddModelError("Input.TargetTiers", T("Hạng được chọn không hợp lệ. Chỉ chọn Đồng, Bạc, Vàng hoặc Kim cương.", "Invalid tier selection. Choose only Bronze, Silver, Gold or Diamond."));
            else tiers.Add(Enum.Parse<MemberTier>(code));
        }
        if (tiers.Count == 0)
            ModelState.AddModelError("Input.TargetTiers", T("Vui lòng chọn ít nhất một hạng thành viên.", "Select at least one membership tier."));
        campaign = new(name, percent, start, end, tiers.Distinct().ToArray());
        return ModelState.IsValid;
    }

    private static bool TryLocalUtc(string? value, out DateTime utc)
    {
        utc = default;
        if (!DateTime.TryParseExact(value, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return false;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local)) return false;
        utc = local.ToUniversalTime();
        return true;
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadAsync();
        if (!Loaded || !ValidateInput(out var campaign)) return Page();
        try
        {
            var result = await MembershipDatabaseService.CreateDiscountAsync(AdminId, campaign);
            if (!result.Success)
            {
                ModelState.AddModelError(string.Empty, result.Error ?? T("Không thể tạo ưu đãi.", "The offer could not be created."));
                return Page();
            }
            TempData["Success"] = Format("Đã tạo ưu đãi và thông báo cho {0} tài khoản đủ điều kiện.", "Offer created; {0} eligible accounts were notified.", result.NotifiedAccounts);
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return Page();
        }
    }

    public Task<IActionResult> OnPostDeactivateAsync(int id) => Mutate(
        () => MembershipDatabaseService.DeactivateDiscountAsync(AdminId, id),
        T("Đã ngừng ưu đãi. Lịch sử và mức giảm của vé đã giữ được bảo toàn.", "Offer deactivated. History and saved discounts on held tickets are retained."));

    public string CampaignStatus(MemberDiscount discount) => !discount.IsActive ? T("Đã ngừng", "Deactivated")
        : NowUtc >= discount.EndsAt ? T("Đã hết hạn", "Expired")
        : NowUtc < discount.StartsAt ? T("Sắp diễn ra", "Upcoming") : T("Đang áp dụng", "Active");

    public static DateTime LocalTime(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
