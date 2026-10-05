using System.Data;
using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using static BusGo.Services.UiText;

namespace BusGo.Pages.Customer;

public abstract class CustomerPageModel : PageModel
{
    public string? Error { get; protected set; }
    public bool IsCounterStaff => CurrentUser.IsAdmin && !CurrentUser.IsOwner;
    protected int AccountId => CurrentUser.Account?.AccountId ?? throw new InvalidOperationException("An authenticated account is required.");

    protected void DatabaseError(Exception exception, string operation)
    {
        LoggerService.LogError(operation, exception);
        Error = T("Không thể tải hoặc lưu thông tin được yêu cầu. Vui lòng kiểm tra kết nối cơ sở dữ liệu hoặc thử lại sau.", "The requested information could not be loaded or saved. Please check the database connection or try again later.");
    }

    protected async Task<TicketDetails?> FindOwnedTicketAsync(int ticketId)
    {
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TicketCode FROM Tickets WHERE TicketId=@TicketId AND AccountId=@AccountId;", connection);
        command.Parameters.Add("@TicketId", SqlDbType.Int).Value = ticketId;
        command.Parameters.Add("@AccountId", SqlDbType.Int).Value = AccountId;
        var code = await command.ExecuteScalarAsync() as string;
        return code is null ? null : await DatabaseHelper.GetTicketByCodeAsync(code);
    }
}
