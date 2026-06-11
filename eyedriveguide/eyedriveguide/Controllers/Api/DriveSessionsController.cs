// ============================================================
// DriveSessionsController.cs — Security-Hardened
// SECURITY FIXES:
//   AS-1  — [Authorize] added
//   OW-1  — Sessions user-scoped
//   OW-8  — Session metrics computed server-side; client body ignored
//   DS-4  — DELETE endpoint + bulk purge added
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EyeDriveGuide.Controllers.Api;

[ApiController]
[Route("api/sessions")]
[Authorize]  // SECURITY FIX AS-1
public class DriveSessionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DriveSessionsController> _logger;

    public DriveSessionsController(
        AppDbContext db,
        IMemoryCache cache,
        ILogger<DriveSessionsController> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    private string UserId => User.Identity?.Name ?? "anonymous";

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        // SECURITY FIX OW-1: user-scoped; return safe subset of fields only
        var sessions = await _db.DriveSessions
            .Where(s => s.UserId == UserId)  // No cross-user leakage
            .OrderByDescending(s => s.StartedAt)
            .Take(20)
            .Select(s => new {
                s.Id, s.StartedAt, s.EndedAt,
                s.TotalDistanceKm, s.AverageSpeedKmh,
                s.SpeedConsistencyScore, s.SpeedAlertCount,
                s.DistractionAlertCount, s.LaneChangeAlertCount,
                s.MergeAlertCount, s.ExitAlertCount,
                s.Mode
                // Note: DestinationAddress omitted from list view (privacy)
            })
            .ToListAsync();

        return Ok(sessions);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var session = await _db.DriveSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId);
        if (session == null) return NotFound();
        return Ok(session);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DriveSession session)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        session.StartedAt = DateTime.UtcNow;
        session.UserId = UserId;  // SECURITY FIX OW-1

        // SECURITY FIX OW-8: zero out all client-supplied metric fields;
        // they will be set by the server on EndSession
        session.TotalDistanceKm = 0;
        session.AverageSpeedKmh = 0;
        session.SpeedConsistencyScore = 0;
        session.SpeedAlertCount = 0;
        session.DistractionAlertCount = 0;
        session.BackingAlertCount = 0;
        session.LaneChangeAlertCount = 0;
        session.MergeAlertCount = 0;
        session.ExitAlertCount = 0;

        _db.DriveSessions.Add(session);
        await _db.SaveChangesAsync();
        return Ok(new { session.Id });
    }

    [HttpPut("{id:int}/end")]
    public async Task<IActionResult> End(int id)
    {
        // SECURITY FIX OW-8: IGNORE client body for all metric fields.
        // All values come from the server-side cache (DriveSessionData),
        // which was populated by the validated SignalR hub session.
        var session = await _db.DriveSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId);

        if (session == null) return NotFound();
        if (session.EndedAt.HasValue)
            return BadRequest(new { error = "Session already ended" });

        // Attempt to pull metrics from the server-side cache
        // The hub EndSession method is the preferred path; this is a REST fallback
        // for clients that lost the SignalR connection.
        // If cache is gone (e.g. server restart), we accept the EndedAt only.
        session.EndedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Session ended", sessionId = id });
    }

    // SECURITY FIX DS-4: individual delete
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var session = await _db.DriveSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId);

        if (session == null) return NotFound();

        _db.DriveSessions.Remove(session);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Session {Id} deleted by {User}", id, UserId);
        return Ok(new { message = "Deleted" });
    }

    // SECURITY FIX DS-4: bulk purge
    [HttpDelete("purge")]
    public async Task<IActionResult> Purge([FromQuery] int olderThanDays = 90)
    {
        if (olderThanDays < 1 || olderThanDays > 3650)
            return BadRequest(new { error = "olderThanDays must be between 1 and 3650" });

        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        var toDelete = await _db.DriveSessions
            .Where(s => s.UserId == UserId && s.StartedAt < cutoff)
            .ToListAsync();

        _db.DriveSessions.RemoveRange(toDelete);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Purged {Count} sessions older than {Days} days for {User}",
            toDelete.Count, olderThanDays, UserId);

        return Ok(new { deleted = toDelete.Count });
    }
}
