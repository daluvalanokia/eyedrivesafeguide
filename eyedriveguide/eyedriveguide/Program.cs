using EyeDriveGuide.Data;
using EyeDriveGuide.Hubs;
using EyeDriveGuide.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

var dbPath = Path.Combine(builder.Environment.ContentRootPath, "eyedriveguide.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<RouteService>();
builder.Services.AddScoped<RouteService>();
builder.Services.AddSingleton<MergeAlgorithm>();
builder.Services.AddSingleton<LaneDisciplineAlgorithm>();
builder.Services.AddSingleton<ExitAlgorithm>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllers();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<DriveHub>("/hubs/drive");

app.Run();
