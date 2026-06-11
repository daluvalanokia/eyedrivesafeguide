// ============================================================
// SimulationController.cs
// Persists and retrieves simulation preference per user.
// Simple: just remembers whether simulation is on/off in the DB
// and what the last-used speed was.
//
// GET  /api/simulation/settings          → {enabled, defaultSpeedKmh}
// POST /api/simulation/settings          → save prefs
// POST /api/simulation/session           → log a sim session (optional analytics)
// ============================================================
using EyeDriveGuide.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Controllers.Api;

[ApiController]
[Route("api/simulation")]
[Authorize]
public class SimulationController(
    AppDbContext db,
    ILogger<SimulationController> logger) : ControllerBase
{
    private string UserId => User.Identity?.Name ?? "anonymous";

    // ── GET /api/simulation/settings ────────────────────────
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        // Stored in UserProfile.SimulationEnabled / SimulationDefaultSpeedKmh
        var profile = await db.UserProfiles.FirstOrDefaultAsync();
        return Ok(new
        {
            enabled          = profile?.SimulationEnabled ?? false,
            defaultSpeedKmh  = profile?.SimulationDefaultSpeedKmh ?? 50
        });
    }

    // ── POST /api/simulation/settings ───────────────────────
    [HttpPost("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] SimSettingsRequest req)
    {
        if (req.DefaultSpeedKmh is < 0 or > 120)
            return BadRequest(new { error = "Speed must be 0–120 km/h" });

        var profile = await db.UserProfiles.FirstOrDefaultAsync();
        if (profile == null)
            return NotFound(new { error = "No user profile found" });

        profile.SimulationEnabled           = req.Enabled;
        profile.SimulationDefaultSpeedKmh   = req.DefaultSpeedKmh;
        await db.SaveChangesAsync();

        logger.LogInformation("Simulation settings saved: enabled={E} speed={S}",
            req.Enabled, req.DefaultSpeedKmh);

        return Ok(new { message = "Saved" });
    }
}

public record SimSettingsRequest(bool Enabled, int DefaultSpeedKmh = 50);
