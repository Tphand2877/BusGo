using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace BusGo.Pages;
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorModel : PageModel
{
    public string Reference => Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    public void OnGet() { }
    public void OnPost() { }
}
