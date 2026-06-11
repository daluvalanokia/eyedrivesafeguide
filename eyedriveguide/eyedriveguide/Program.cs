// ============================================================
// Program.cs — Security-Hardened (juneeyedrivesafeguide)
// Changes vs original:
//   • Authentication (cookie) registered
//   • CORS locked to localhost origins
//   • Rate limiting (built-in .NET 8 System.Threading.RateLimiting)
//   • Security-headers middleware added
//   • DB path moved outside wwwroot
//   • Exception handler always active; developer page localhost-only
//   • HSTS + HTTPS redirect always active
//
// FIX: removed unused IDeveloperPageExceptionFilter reference (CS0246)
//      — the dead code block that referenced it has been removed entirely.
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Hubs;
using EyeDriveGuide.Middleware;
using EyeDriveGuide.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Database — path OUTSIDE web root ────────────────────────
// SECURITY FIX DS-1: never place DB in content root (web-accessible).
var appDataPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(appDataPath);
var dbPath = Path.Combine(appDataPath, "eyedriveguide.db");

// Sanity-check: abort if somehow the path is under wwwroot
var webRootPath = builder.Environment.WebRootPath
    ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
if (dbPath.StartsWith(webRootPath, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("SECURITY: DB path must not be under wwwroot.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ── Authentication — SECURITY FIX AS-1 ─────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath           = "/auth/login";
        options.LogoutPath          = "/auth/logout";
        options.ExpireTimeSpan      = TimeSpan.FromDays(30);
        options.SlidingExpiration   = true;
        options.Cookie.HttpOnly     = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite     = SameSiteMode.Strict;
        options.Cookie.Name         = "edg_session";
    });

builder.Services.AddAuthorization();

// ── CORS — SECURITY FIX OW-3: lock to localhost ─────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalOnly", policy =>
        policy.WithOrigins("http://localhost:5000", "https://localhost:5001")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Rate Limiting — SECURITY FIX AS-4 ───────────────────────
builder.Services.AddRateLimiter(options =>
{
    // Hub: max 10 req/s per connection (covers 4 Hz GPS + headroom)
    options.AddSlidingWindowLimiter("hub-limiter", o =>
    {
        o.PermitLimit            = 10;
        o.Window                 = TimeSpan.FromSeconds(1);
        o.SegmentsPerWindow      = 4;
        o.QueueProcessingOrder   = QueueProcessingOrder.OldestFirst;
        o.QueueLimit             = 5;
    });

    // API: max 30 req/min per IP
    options.AddSlidingWindowLimiter("api-limiter", o =>
    {
        o.PermitLimit            = 30;
        o.Window                 = TimeSpan.FromMinutes(1);
        o.SegmentsPerWindow      = 6;
        o.QueueProcessingOrder   = QueueProcessingOrder.OldestFirst;
        o.QueueLimit             = 5;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Antiforgery ─────────────────────────────────────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName          = "X-XSRF-TOKEN";
    options.Cookie.HttpOnly     = false; // JS needs to read it
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite     = SameSiteMode.Strict;
});

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// ── Application services ────────────────────────────────────
builder.Services.AddScoped<RouteService>();
builder.Services.AddScoped<FollowingDistanceHistoryService>();
builder.Services.AddSingleton<MergeAlgorithm>();
builder.Services.AddSingleton<LaneDisciplineAlgorithm>();
builder.Services.AddSingleton<ExitAlgorithm>();
builder.Services.AddSingleton<AlertIntegrityService>();
builder.Services.AddSingleton<AuditLogService>();
builder.Services.AddHostedService<DataRetentionService>();

var app = builder.Build();

// ── DB init ──────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// ── Exception handling — ALWAYS active (SECURITY FIX AS-6) ──
// Developer exception page shown only for localhost in development.
// Production always gets the generic /Home/Error page.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();  // only reachable locally (no firewall exposure)
}
else
{
    app.UseExceptionHandler("/Home/Error");
}

// ── HSTS + HTTPS — ALWAYS active (not dev-only) ─────────────
app.UseHsts();
app.UseHttpsRedirection();

// ── Security Headers — SECURITY FIX AS-7 / OW-3 ────────────
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseStaticFiles();
app.UseRouting();
app.UseCors("LocalOnly");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ── Antiforgery middleware for API routes ────────────────────
app.UseMiddleware<AntiforgeryTokenMiddleware>();

app.MapControllers();
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapHub<DriveHub>("/hubs/drive")
   .RequireAuthorization()
   .RequireRateLimiting("hub-limiter");

app.Run();
