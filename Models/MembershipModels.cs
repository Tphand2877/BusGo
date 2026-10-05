using static BusGo.Services.UiText;

namespace BusGo.Models;

public enum MemberTier
{
    Standard,
    Bronze,
    Silver,
    Gold,
    Diamond
}

public static class MembershipLabels
{
    public static string Display(MemberTier tier) => tier switch
    {
        MemberTier.Standard => T("Tiêu chuẩn", "Standard"),
        MemberTier.Bronze => T("Đồng", "Bronze"),
        MemberTier.Silver => T("Bạc", "Silver"),
        MemberTier.Gold => T("Vàng", "Gold"),
        MemberTier.Diamond => T("Kim cương", "Diamond"),
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };
}

public sealed record MembershipTierInfo(MemberTier Tier, decimal MinimumSpend)
{
    public string DisplayName => MembershipLabels.Display(Tier);
}

public sealed record MemberDiscount(
    int DiscountId,
    string Name,
    decimal Percent,
    DateTime StartsAt,
    DateTime EndsAt,
    bool IsActive,
    IReadOnlyList<MemberTier> TargetTiers);

public sealed record MembershipOverview(
    decimal QualifiedSpend,
    MembershipTierInfo CurrentTier,
    MembershipTierInfo? NextTier,
    IReadOnlyList<MembershipTierInfo> Tiers,
    IReadOnlyList<MemberDiscount> Discounts,
    int UnreadNotifications)
{
    public decimal RemainingSpend => NextTier is { } next ? Math.Max(0, next.MinimumSpend - QualifiedSpend) : 0;
    public decimal ProgressPercent => NextTier is { } next
        ? Math.Clamp((QualifiedSpend - CurrentTier.MinimumSpend) / (next.MinimumSpend - CurrentTier.MinimumSpend) * 100m, 0m, 100m)
        : 100m;
}

public sealed record MembershipDiscountInput(
    string Name,
    decimal Percent,
    DateTime StartsAt,
    DateTime EndsAt,
    IReadOnlyCollection<MemberTier> TargetTiers);

public readonly record struct DiscountCreationResult(bool Success, int DiscountId, int NotifiedAccounts, string? Error);

public readonly record struct BookingDiscountQuote(
    decimal BaseFare,
    MemberTier Tier,
    int? DiscountId,
    string? DiscountName,
    decimal Percent,
    decimal DiscountAmount)
{
    public decimal TotalAmount => BaseFare - DiscountAmount;
}

public sealed record MemberNotification(
    long NotificationId,
    int DiscountId,
    MemberTier Tier,
    string DiscountName,
    decimal Percent,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTime CreatedAt,
    DateTime? ReadAt,
    bool DiscountActive);
