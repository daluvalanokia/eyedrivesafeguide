// ============================================================
// AntiforgeryTokenMiddleware.cs — SECURITY FIX AS-2 / OW-1
// Validates CSRF token on all mutating API requests.
// JS clients must read the cookie "XSRF-TOKEN" and send it
// as the request header "X-XSRF-TOKEN".
// ============================================================
using Microsoft.AspNetCore.Antiforgery;

namespace EyeDriveGuide.Middleware;

public class AntiforgeryTokenMiddleware(RequestDelegate next, IAntiforgery antiforgery,
    ILogger<AntiforgeryTokenMiddleware> logger)
{
    // Methods that require CSRF validation
    private static readonly HashSet<string> _mutatingMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    // Routes that are excluded (auth endpoints need special handling)
    private static readonly HashSet<string> _excluded =
        new(StringComparer.OrdinalIgnoreCase) { "/auth/login", "/auth/logout" };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Inject the antiforgery cookie on GET requests so JS can read it
        if (HttpMethods.IsGet(context.Request.Method))
        {
            antiforgery.SetCookieTokenAndHeader(context);
        }

        // Validate on mutating API requests
        if (_mutatingMethods.Contains(context.Request.Method) &&
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !_excluded.Contains(path))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException ex)
            {
                logger.LogWarning("CSRF validation failed for {Path} from {IP}: {Message}",
                    path, context.Connection.RemoteIpAddress, ex.Message);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Invalid or missing CSRF token.",
                    code = "CSRF_VALIDATION_FAILED"
                });
                return;
            }
        }

        await next(context);
    }
}
