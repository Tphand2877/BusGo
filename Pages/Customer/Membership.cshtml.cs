using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Authorization;

namespace BusGo.Pages.Customer;

[Authorize(Roles = "Customer")]
public sealed class MembershipModel : CustomerPageModel
{
    public MembershipOverview? Overview { get; private set; }
    public IReadOnlyList<MemberDiscount> Discounts { get; private set; } = [];
    public DateTime EvaluatedAtUtc { get; private set; }

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            Overview = await MembershipDatabaseService.GetOverviewAsync(AccountId);
            EvaluatedAtUtc = DateTime.UtcNow;
            Discounts = Overview.Discounts
                .Where(discount => discount.IsActive && discount.EndsAt > EvaluatedAtUtc && discount.TargetTiers.Contains(Overview.CurrentTier.Tier))
                .OrderBy(discount => discount.StartsAt > EvaluatedAtUtc)
                .ThenByDescending(discount => discount.Percent)
                .ThenBy(discount => discount.DiscountId)
                .ToArray();
        }
        catch (Exception ex) { DatabaseError(ex, "Loading customer membership failed."); }
    }
}
