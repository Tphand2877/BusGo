using System.Security.Claims;
using BusGo.Data;
using BusGo.Models;
using BusGo.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using System.Globalization;
using BusGo;
using Microsoft.AspNetCore.Localization;
using static BusGo.Services.UiText;

using Microsoft.AspNetCore.HttpOverrides;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var cultures = new[] { new CultureInfo("vi-VN"), new CultureInfo("en-US") };
    options.DefaultRequestCulture = new RequestCulture("vi-VN");
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
    options.RequestCultureProviders = [new CookieRequestCultureProvider { CookieName = "BusGo.Language" }];
});
builder.Services.AddRazorPages().AddMvcOptions(options =>
{
    var messages = options.ModelBindingMessageProvider;
    messages.SetAttemptedValueIsInvalidAccessor((value, field) => Format("Giá trị '{0}' không hợp lệ cho {1}.", "The value '{0}' is not valid for {1}.", value, field));
    messages.SetNonPropertyAttemptedValueIsInvalidAccessor(value => Format("Giá trị '{0}' không hợp lệ.", "The value '{0}' is not valid.", value));
    messages.SetValueMustNotBeNullAccessor(field => T("Vui lòng nhập giá trị.", "Please enter a value."));
    messages.SetValueMustBeANumberAccessor(field => Format("{0} phải là số.", "{0} must be a number.", field));
    messages.SetNonPropertyValueMustBeANumberAccessor(() => T("Vui lòng nhập một số.", "Please enter a number."));
    messages.SetMissingBindRequiredValueAccessor(field => Format("Vui lòng nhập {0}.", "Please enter {0}.", field));
    messages.SetMissingKeyOrValueAccessor(() => T("Vui lòng nhập giá trị.", "Please enter a value."));
    messages.SetMissingRequestBodyRequiredValueAccessor(() => T("Thiếu nội dung yêu cầu.", "A request body is required."));
    messages.SetUnknownValueIsInvalidAccessor(field => Format("Giá trị không hợp lệ cho {0}.", "The value is not valid for {0}.", field));
    messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => T("Giá trị không hợp lệ.", "The value is not valid."));
}).AddDataAnnotationsLocalization(options =>
    options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(ValidationMessages)));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "BusGo.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Error";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = async context =>
    {
        if (!int.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            context.RejectPrincipal();
            return;
        }
        // Refresh from the database: another administrator's role changes take effect immediately.
        await using var connection = DatabaseHelper.GetConnection();
        await connection.OpenAsync(context.HttpContext.RequestAborted);
        await using var command = new SqlCommand("SELECT Username, FullName, Email, Phone, Role FROM Accounts WHERE AccountId=@Id", connection);
        command.Parameters.AddWithValue("@Id", id);
        await using var reader = await command.ExecuteReaderAsync(context.HttpContext.RequestAborted);
        if (!await reader.ReadAsync(context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
            return;
        }
        string Value(int column) => reader.IsDBNull(column) ? "" : reader.GetString(column);
        var role = Value(4);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, Value(0)),
            new(ClaimTypes.GivenName, Value(1)), new(ClaimTypes.Email, Value(2)),
            new(ClaimTypes.MobilePhone, Value(3)), new(ClaimTypes.Role, role)
        };
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }
        context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanGrantAdmin", policy => policy.RequireRole("Owner"));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddHostedService<WebLifecycleService>();
var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler("/Error");
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https://img.vietqr.io; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRequestLocalization();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var user = context.User;
    CurrentUser.Account = user.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? new UserSession(id, user.Identity.Name ?? "", user.FindFirstValue(ClaimTypes.GivenName) ?? "",
            user.FindFirstValue(ClaimTypes.Email), user.FindFirstValue(ClaimTypes.MobilePhone), user.FindFirstValue(ClaimTypes.Role) ?? "Customer")
        : null;
    try { await next(); }
    finally { CurrentUser.Account = null; }
});
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { application = "BusGo", status = "ready" }));
const int maxRetries = 12;
var migrated = false;
for (var attempt = 1; attempt <= maxRetries; attempt++)
{
    if (await DatabaseMigrator.MigrateAsync())
    {
        migrated = true;
        break;
    }
    LoggerService.LogWarning($"Database migration attempt {attempt}/{maxRetries} failed. Retrying in 5 seconds... (Error: {DatabaseMigrator.LastError})");
    await Task.Delay(TimeSpan.FromSeconds(5));
}
if (!migrated)
    throw new InvalidOperationException($"BusGo database initialization failed after {maxRetries} attempts. Check SQL Server configuration and application logs: {DatabaseMigrator.LastError}");
await DatabaseMigrator.EnsureDefaultOwnerAsync(app.Configuration);
if (app.Configuration.GetValue<bool>("Database:SeedUpcomingTrips"))
    await DatabaseMigrator.EnsureUpcomingTripsAsync();
await app.RunAsync();
