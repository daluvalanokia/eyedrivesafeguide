// ============================================================
// FollowingDistanceController.cs
// REST API for following distance history and baselines.
//
// GET    /api/following/history          → last 20 rides (all bands)
// GET    /api/following/history/{band}   → last 10 rides for one band
// GET    /api/following/baseline         → baselines for all bands
// GET    /api/following/baseline/{band}  → baseline for one band
// DELETE /api/following/history/{id}     → remove a ride record
//
// FIX CS1998: DeleteHistoryEntry now properly awaits the async delete
//             call instead of being a no-op sync method.
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using EyeDriveGuide.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Controllers.Api;

[ApiController]
[Route("api/following")]
[Authorize]
public class FollowingDistanceController(
    FollowingDistanceHistoryService historyService,
    AppDbContext db,
    ILogger<FollowingDistanceController> logger) : ControllerBase
{
    private string UserId => User.Identity?.Name ?? "anonymous";

    // ── GET /api/following/history ───────────────────────────
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory()
    {
        var history = await historyService.GetHistoryAsync(UserId);
        return Ok(history.Select(h => new {
            h.Id, h.Band, h.RideEndedAt,
            h.AverageDistanceM, h.MedianDistanceM, h.MinDistanceM,
            h.AlertCount, h.SampleCount, h.BaselineAtTimeM,
            h.AdverseConditions
        }));
    }

    // ── GET /api/following/history/{band} ────────────────────
    [HttpGet("history/{band}")]
    public async Task<IActionResult> GetHistoryForBand(SpeedBand band)
    {
        if (!Enum.IsDefined(band))
            return BadRequest(new { error = "Invalid speed band" });

        var history = await historyService.GetHistoryAsync(UserId, band, 10);
        return Ok(history.Select(h => new {
            h.Id, h.Band, h.RideEndedAt,
            h.AverageDistanceM, h.MedianDistanceM,
            h.AlertCount, h.SampleCount, h.BaselineAtTimeM
        }));
    }

    // ── GET /api/following/baseline ──────────────────────────
    [HttpGet("baseline")]
    public async Task<IActionResult> GetAllBaselines()
    {
        var baselines = await historyService.GetAllBaselinesAsync(UserId);
        return Ok(baselines.Select(kv => new {
            band      = kv.Key.ToString(),
            bandId    = (int)kv.Key,
            baselineM = Math.Round(kv.Value, 1),
            hasData   = kv.Value > 0
        }));
    }

    // ── GET /api/following/baseline/{band} ───────────────────
    [HttpGet("baseline/{band}")]
    public async Task<IActionResult> GetBaseline(SpeedBand band)
    {
        if (!Enum.IsDefined(band))
            return BadRequest(new { error = "Invalid speed band" });

        var baseline = await historyService.GetPersonalBaselineAsync(UserId, band);
        return Ok(new {
            band      = band.ToString(),
            baselineM = Math.Round(baseline, 1),
            hasData   = baseline > 0,
            message   = baseline <= 0
                ? "Not enough ride history yet (minimum 3 rides needed)"
                : $"Your typical following distance at {BandDescription(band)} is {baseline:F0} m"
        });
    }

    // ── DELETE /api/following/history/{id} ───────────────────
    // FIX CS1998: properly await the DB delete so the method is genuinely async.
    [HttpDelete("history/{id:int}")]
    public async Task<IActionResult> DeleteHistoryEntry(int id)
    {
        var entry = await db.FollowingDistanceHistory
            .FirstOrDefaultAsync(h => h.Id == id && h.UserId == UserId);

        if (entry == null)
            return NotFound(new { error = "Record not found or not owned by you" });

        db.FollowingDistanceHistory.Remove(entry);
        await db.SaveChangesAsync();

        logger.LogInformation("Deleted following distance history {Id} for user {User}", id, UserId);
        return Ok(new { message = "Deleted" });
    }

    private static string BandDescription(SpeedBand band) => band switch
    {
        SpeedBand.UrbanLow => "0–25 km/h (urban)",
        SpeedBand.UrbanMid => "26–45 km/h (urban mid)",
        SpeedBand.Suburban => "46–55 km/h (suburban)",
        SpeedBand.Highway  => "56–65 km/h (highway)",
        SpeedBand.Freeway  => "66+ km/h (freeway)",
        _ => "unknown"
    };
}
