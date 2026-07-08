using Allegro.Admin.Components;
using Allegro.Admin.Services;
using Allegro.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<ToastService>();

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

app.MapGet($"/{CSVMaker.FileName}",
    () => Results.File(Path.Combine(SaverExtensions.ResourceDirectory, CSVMaker.FileName), "text/csv"));

app.Run();