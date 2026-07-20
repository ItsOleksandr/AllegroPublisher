using System.Security.Claims;
using Allegro.Admin.Components;
using Allegro.Admin.Models;
using Allegro.Admin.Services;
using Allegro.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<ToastService>();
builder.Services.AddSingleton<Saver<SaveData>>( _ => new Saver<SaveData>("admin_options.txt"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AllegroPublishService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
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

    if (password == expectedPassword)
    {
        var claims = new List<Claim> { new Claim(ClaimTypes.Name, "Admin") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(new ClaimsPrincipal(identity));
        
        return Results.Redirect("/");
    }
    
    return Results.Redirect("/login?failed=true");
});

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/login");
});

app.MapGet($"/{CSVMaker.FileName}", (HttpContext context) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    
    string filePath = Path.Combine(SaverExtensions.ResourceDirectory, CSVMaker.FileName);
    
    return Results.File(filePath, "text/csv");
});

app.Run();