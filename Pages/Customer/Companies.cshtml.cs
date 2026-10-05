using BusGo.Data;
using BusGo.Models;

namespace BusGo.Pages.Customer;

public sealed class CompaniesModel : CustomerPageModel
{
    public IReadOnlyList<BusCompanyItem> Companies { get; private set; } = [];
    public async Task OnGetAsync()
    {
        try { Companies = await DatabaseHelper.GetBusCompaniesAsync(); }
        catch (Exception ex) { DatabaseError(ex, "Loading bus companies failed."); }
    }
}
