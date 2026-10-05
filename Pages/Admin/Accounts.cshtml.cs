using static BusGo.Services.UiText;
using BusGo.Data;
using BusGo.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace BusGo.Pages.Admin;

public sealed class AccountsModel : AdminPageModel
{
    private readonly IAuthorizationService _authorizationService;

    public AccountsModel(IAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? RoleFilter { get; set; }
    public IReadOnlyList<AccountAdminItem> Accounts { get; private set; } = [];
    public IReadOnlyList<RoleAuditLogItem> RecentRoleLogs { get; private set; } = [];
    public int CurrentAccountId => AdminId;
    public bool ViewerIsOwner => User.IsInRole("Owner");
    public Task OnGetAsync() => LoadSafely(async () =>
    {
        var rows = await AdminDatabaseService.GetAccountsAsync();
        IEnumerable<AccountAdminItem> query = rows;

        if (!string.IsNullOrWhiteSpace(RoleFilter) && !string.Equals(RoleFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(a => string.Equals(a.Role, RoleFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            query = query.Where(a =>
                a.Username.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                a.FullName.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                (a.Email?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Phone?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        Accounts = query.ToList();
        if (ViewerIsOwner)
        {
            RecentRoleLogs = await AdminDatabaseService.GetRecentRoleAuditLogsAsync();
        }
    });
    public async Task<IActionResult> OnPostRoleAsync(int id, string role, string? confirmPassword)
    {
        var targetRole = await AdminDatabaseService.GetAccountRoleAsync(id);
        bool touchesPrivileged = role is "Admin" or "Owner" || targetRole is "Admin" or "Owner";
        if (touchesPrivileged)
        {
            var authResult = await _authorizationService.AuthorizeAsync(User, "CanGrantAdmin");
            if (!authResult.Succeeded)
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        return await Mutate(
            () => AdminDatabaseService.UpdateAccountRoleAsync(id, role, confirmPassword),
            T("Đã cập nhật vai trò tài khoản.", "Account role updated."),
            new { Search, RoleFilter });
    }
    public Task<IActionResult> OnPostPasswordAsync(int id, string password) => Mutate(
        () => AdminDatabaseService.ResetAccountPasswordAsync(id, password), T("Đã đặt lại mật khẩu. Hãy chia sẻ mật khẩu mới với chủ tài khoản một cách an toàn.", "Password reset. Share the new password with the account owner securely."), new { Search, RoleFilter });
    public Task<IActionResult> OnPostDeleteAsync(int id) => Mutate(
        () => AdminDatabaseService.DeleteAccountAsync(id), T("Đã xóa tài khoản.", "Account deleted."), new { Search, RoleFilter });
}
