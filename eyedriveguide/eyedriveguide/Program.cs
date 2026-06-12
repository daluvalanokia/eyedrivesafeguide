// ============================================================
// Program.cs — Security-Hardened (juneeyedrivesafeguide)
//
// FIX (runtime): Antiforgery SecurePolicy changed from Always
//   to SameAsRequest in Development so HTTP localhost requests
//   don't crash. Production keeps Always (runs behind HTTPS).
//
// FIX (runtime): HSTS and HTTPS redirect suppressed in
//   Development — UseHttpsRedirection() on a plain HTTP dev
//   server causes redirect loops and breaks the antiforgery fix.
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

// ── Database — path OUTSIDE web root (SECURITY FIX DS-1) ────
var appDataPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(appDataPath);
var dbPath = Path.Combine(appDataPath, "eyedriveguide.db");

var webRootPath = builder.Environment.WebRootPath
    ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
if (dbPath.StartsWith(webRootPath, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("SECURITY: DB path must not be under wwwroot.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ── Authentication (SECURITY FIX AS-1) ──────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath         = "/auth/login";
        options.LogoutPath        = "/auth/logout";
        options.ExpireTimeSpan    = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly   = true;
        // FIX: SameAsRequest in dev (HTTP), Always in production (HTTPS)
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Name     = "edg_session";
    });

builder.Services.AddAuthorization();

// ── CORS (SECURITY FIX OW-3) ────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalOnly", policy =>
        policy.WithOrigins("http://localhost:5000", "https://localhost:5001",
                           "http://localhost:5193","https://localhost:7193")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Rate Limiting (SECURITY FIX AS-4) ───────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.AddSlidingWindowLimiter("hub-limiter", o =>
    {
        o.PermitLimit          = 10;
        o.Window               = TimeSpan.FromSeconds(1);
        o.SegmentsPerWindow    = 4;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit           = 5;
    });
    options.AddSlidingWindowLimiter("api-limiter", o =>
    {
        o.PermitLimit          = 30;
        o.Window               = TimeSpan.FromMinutes(1);
        o.SegmentsPerWindow    = 6;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit           = 5;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Antiforgery ──────────────────────────────────────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName      = "X-XSRF-TOKEN";
    options.Cookie.HttpOnly = false; // JS must read it
    // FIX: match SecurePolicy to environment — avoids the
    //   "not an SSL request" InvalidOperationException in dev
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// ── Application services ─────────────────────────────────────
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

// ── Exception handling ───────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // FIX: HSTS + HTTPS redirect only in production.
    // In development the app runs over plain HTTP (launchSettings port 5193).
    // Enabling these in dev causes redirect loops and triggers the
    // antiforgery SecurePolicy = Always crash.
    app.UseHsts();
    app.UseHttpsRedirection();
}

// ── Security Headers (SECURITY FIX AS-7) ────────────────────
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseStaticFiles();
app.UseRouting();
app.UseCors("LocalOnly");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ── Antiforgery middleware ───────────────────────────────────
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
