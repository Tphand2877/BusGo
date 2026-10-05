using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static BusGo.Services.UiText;

namespace BusGo.Pages.Customer;

[Authorize(Roles = "Customer")]
public sealed class NotificationsModel : CustomerPageModel
{
    public IReadOnlyList<MemberNotification> Notifications { get; private set; } = [];
    public MembershipOverview? Overview { get; private set; }
    public DateTime EvaluatedAtUtc { get; private set; }

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            Overview = await MembershipDatabaseService.GetOverviewAsync(AccountId);
            Notifications = await MembershipDatabaseService.GetNotificationsAsync(AccountId);
            EvaluatedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { DatabaseError(ex, "Loading customer membership notifications failed."); }
    }

    public string Availability(MemberNotification notification)
    {
        if (!notification.DiscountActive) return T("Đã ngừng kích hoạt — không áp dụng", "Deactivated — not applicable");
        if (notification.EndsAt <= EvaluatedAtUtc) return T("Đã hết hạn — không áp dụng", "Expired — not applicable");
        if (Overview is null || !Overview.Discounts.Any(discount => discount.DiscountId == notification.DiscountId && discount.TargetTiers.Contains(Overview.CurrentTier.Tier)))
            return T("Hạng hiện tại không đủ điều kiện", "Your current tier does not qualify");
        if (notification.StartsAt > EvaluatedAtUtc) return T("Sắp tới — chưa áp dụng", "Upcoming — not yet applicable");
        return T("Đang hiệu lực cho hạng hiện tại", "Available for your current tier");
    }

    public async Task<IActionResult> OnPostMarkReadAsync(long notificationId)
    {
        try
        {
            if (notificationId <= 0 || !await MembershipDatabaseService.MarkNotificationReadAsync(AccountId, notificationId))
                TempData["Error"] = T("Không tìm thấy thông báo trong tài khoản của bạn.", "This notification was not found in your account.");
            else
                TempData["Success"] = T("Đã đánh dấu thông báo là đã đọc.", "The notification was marked as read.");
        }
        catch (Exception ex)
        {
            DatabaseError(ex, "Marking customer membership notification as read failed.");
            TempData["Error"] = Error;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMarkAllReadAsync()
    {
        try
        {
            await MembershipDatabaseService.MarkAllNotificationsReadAsync(AccountId);
            TempData["Success"] = T("Đã đánh dấu tất cả thông báo là đã đọc.", "All notifications were marked as read.");
        }
        catch (Exception ex)
        {
            DatabaseError(ex, "Marking all customer membership notifications as read failed.");
            TempData["Error"] = Error;
        }
        return RedirectToPage();
    }
}
