// ============================================================
// SecurityHeadersMiddleware.cs — SECURITY FIX AS-7 / OW-3
// Adds: CSP, X-Frame-Options, X-Content-Type-Options,
//       Referrer-Policy, Permissions-Policy, HSTS supplement
// ============================================================
namespace EyeDriveGuide.Middleware;

public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Content-Security-Policy
        // Allowlist:
        //   scripts  — self + trusted CDNs with SRI (see Views)
        //   connect  — self only for SignalR WebSocket/SSE
        //   media    — self (mic/camera via browser APIs, no src element)
        //   style    — self + bootstrap CDN
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' https://cdn.jsdelivr.net https://unpkg.com; " +
            "style-src 'self' https://cdn.jsdelivr.net https://unpkg.com; " +
            "connect-src 'self' wss://localhost:5001 ws://localhost:5000 " +
                "https://api.openrouteservice.org https://api.open-meteo.com " +
                "https://api.sunrise-sunset.org; " +
            "img-src 'self' data: https://*.tile.openstreetmap.org; " +
            "media-src 'self'; " +
            "font-src 'self'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self';";

        // Clickjacking protection
        headers["X-Frame-Options"] = "DENY";

        // MIME sniffing protection
        headers["X-Content-Type-Options"] = "nosniff";

        // Referrer — don't leak URL to external services
        headers["Referrer-Policy"] = "no-referrer";

        // Permissions-Policy — restrict sensor APIs to self
        // Camera/mic/geolocation are needed by the app but only from self
        headers["Permissions-Policy"] =
            "geolocation=(self), " +
            "microphone=(self), " +
            "camera=(self), " +
            "accelerometer=(self), " +
            "gyroscope=(self), " +
            "magnetometer=(), " +
            "payment=(), " +
            "usb=()";

        // Remove server identification headers
        headers.Remove("Server");
        headers.Remove("X-Powered-By");
        headers.Remove("X-AspNet-Version");
        headers.Remove("X-AspNetMvc-Version");

        await next(context);
    }
}
