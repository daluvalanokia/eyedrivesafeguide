// ============================================================
// AuthController.cs — SECURITY FIX AS-1
// Provides simple device-token authentication for single-user
// mode. In multi-user mode, replace with JWT or ASP.NET Identity.
//
// POST /auth/login   { "deviceToken": "..." }  → sets auth cookie
// POST /auth/logout                             → clears cookie
// GET  /auth/me                                 → returns current user
// ============================================================
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;

namespace EyeDriveGuide.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration config, ILogger<AuthController> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ── Single-user device token login ──────────────────────
    // Set your token via environment variable: EDG_DEVICE_TOKEN
    // Generate: openssl rand -hex 32
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceToken))
            return BadRequest(new { error = "Device token required" });

        var expectedToken = _config["EDG_DEVICE_TOKEN"]
                         ?? Environment.GetEnvironmentVariable("EDG_DEVICE_TOKEN");

        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            _logger.LogError("EDG_DEVICE_TOKEN not configured — login rejected");
            return StatusCode(503, new { error = "Authentication not configured" });
        }

        // Constant-time comparison to prevent timing attacks
        var match = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(req.DeviceToken.PadRight(64)),
            System.Text.Encoding.UTF8.GetBytes(expectedToken.PadRight(64))
        );

        if (!match)
        {
            _logger.LogWarning("Failed login attempt from {IP}",
                HttpContext.Connection.RemoteIpAddress);

            // Add artificial delay to slow brute-force
            await Task.Delay(Random.Shared.Next(200, 500));
            return Unauthorized(new { error = "Invalid device token" });
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "driver"),
            new(ClaimTypes.NameIdentifier, "driver-1"),
            new(ClaimTypes.Role, "Driver")
        };

        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            });

        _logger.LogInformation("Successful login from {IP}",
            HttpContext.Connection.RemoteIpAddress);

        return Ok(new { message = "Authenticated", userId = "driver-1" });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { message = "Logged out" });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (!User.Identity?.IsAuthenticated ?? true)
            return Unauthorized();

        return Ok(new
        {
            userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            name = User.Identity?.Name,
            role = User.FindFirst(ClaimTypes.Role)?.Value
        });
    }
}

public record LoginRequest(string DeviceToken);
