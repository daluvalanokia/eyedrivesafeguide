// ============================================================
// AntiforgeryTokenMiddleware.cs — SECURITY FIX AS-2 / OW-1
// Validates CSRF token on all mutating API requests.
// JS clients must read the cookie "XSRF-TOKEN" and send it
// as the request header "X-XSRF-TOKEN".
//
// FIX (runtime): SetCookieTokenAndHeader only called on HTTPS
//   requests (or when running in Development over HTTP).
//   Prevents InvalidOperationException from AntiforgeryOptions
//   .Cookie.SecurePolicy = Always on plain-HTTP dev requests.
// ============================================================
using Microsoft.AspNetCore.Antiforgery;

namespace EyeDriveGuide.Middleware;

public class AntiforgeryTokenMiddleware(
    RequestDelegate next,
    IAntiforgery antiforgery,
    IWebHostEnvironment env,
    ILogger<AntiforgeryTokenMiddleware> logger)
{
    // Methods that require CSRF validation
    private static readonly HashSet<string> _mutatingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    // Routes excluded from CSRF (auth endpoints handle their own tokens)
    private static readonly HashSet<string> _excluded =
        new(StringComparer.OrdinalIgnoreCase) { "/auth/login", "/auth/logout" };

    public async Task InvokeAsync(HttpContext context)
    {
        var path  = context.Request.Path.Value ?? string.Empty;
        var isGet = HttpMethods.IsGet(context.Request.Method);

        // FIX: only inject the antiforgery cookie when the request is HTTPS,
        // OR we are in Development (where HTTPS redirect hasn't fired yet).
        // This prevents the runtime crash:
        //   InvalidOperationException: AntiforgeryOptions.Cookie.SecurePolicy = Always
        //   but current request is not an SSL request.
        if (isGet && (context.Request.IsHttps || env.IsDevelopment()))
        {
            antiforgery.SetCookieTokenAndHeader(context);
        }

        // Validate CSRF on mutating API requests
        if (_mutatingMethods.Contains(context.Request.Method) &&
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !_excluded.Contains(path))
        {
            // Only validate when HTTPS (or dev) — same guard as above
            if (context.Request.IsHttps || env.IsDevelopment())
            {
                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException ex)
                {
                    logger.LogWarning(
                        "CSRF validation failed for {Path} from {IP}: {Message}",
                        path, context.Connection.RemoteIpAddress, ex.Message);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Invalid or missing CSRF token.",
                        code  = "CSRF_VALIDATION_FAILED"
                    });
                    return;
                }
            }
        }

        await next(context);
    }
}
