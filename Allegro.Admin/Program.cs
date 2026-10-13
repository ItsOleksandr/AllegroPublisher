using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Allegro.Admin.Components;
using Allegro.Admin.Models;
using Allegro.Admin.Services;
using Allegro.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<ToastService>();
builder.Services.AddSingleton<Saver<SaveData>>( _ => new Saver<SaveData>("admin_options.txt"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AllegroPublishService>();
builder.Services.AddHostedService<AllegroTokenRefresher>();
builder.Services.AddDataProtection()
    .SetApplicationName("Allegro.Admin")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(SaverExtensions.ResourceDirectory, "DataProtection-Keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnValidatePrincipal = context =>
        {
            var expected = PasswordStamp(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Auth:Password"]);
            if (context.Principal?.FindFirst(PasswordStampClaim)?.Value != expected)
            {
                context.RejectPrincipal();
            }
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAuthentication();
app.UseAuthorization();

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapPost("/auth/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var password = form["password"].ToString();

    var expectedPassword = ctx.RequestServices
        .GetRequiredService<IConfiguration>()["Auth:Password"];

    if (!string.IsNullOrEmpty(expectedPassword)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(expectedPassword)))
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, "Admin"),
            new Claim(PasswordStampClaim, PasswordStamp(expectedPassword)),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365),
            AllowRefresh = true,
        });
        
        return Results.Redirect("/");
    }
    
    return Results.Redirect("/login?failed=true");
});

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/login");
});

app.Run();

static string PasswordStamp(string? password) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("allegro-admin:" + (password ?? ""))))[..16];

partial class Program
{
    private const string PasswordStampClaim = "password-stamp";
}