using Allegro.Admin.Components;
using Allegro.Admin.Models;
using Allegro.Admin.Services;
using Allegro.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<ToastService>();
builder.Services.AddSingleton<Saver<SaveData>>( _ => new Saver<SaveData>("admin_options.txt"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet($"/{CSVMaker.FileName}", (HttpContext context) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    
    string filePath = Path.Combine(SaverExtensions.ResourceDirectory, CSVMaker.FileName);
    
    return Results.File(filePath, "text/csv");
});

app.Run();