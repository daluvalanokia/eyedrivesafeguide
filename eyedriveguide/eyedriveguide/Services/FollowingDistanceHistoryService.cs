// ============================================================
// FollowingDistanceHistoryService.cs
// Manages the 10-ride rolling average per user per speed band.
//
// After each ride ends:
//   1. Computes per-band summary from the session's DistanceSamples
//   2. Saves to FollowingDistanceHistory (max 10 per user per band)
//   3. Returns the updated personal baseline for use in next ride
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Services;

public class FollowingDistanceHistoryService(
    IServiceScopeFactory scopeFactory,
    ILogger<FollowingDistanceHistoryService> logger)
{
    // Maximum ride records kept per user per speed band
    private const int MaxHistoryPerBand = 10;

    // Minimum samples needed to count a band as "valid data" for the ride
    private const int MinSamplesForBand = 20;

    // ── Save ride summary ────────────────────────────────────
    /// <summary>
    /// Called at session end. Computes per-band summaries from raw samples,
    /// persists them, and prunes records beyond the 10-ride window.
    /// Returns: dictionary of band → new personal baseline (metres).
    /// </summary>
    public async Task<Dictionary<SpeedBand, double>> SaveRideSummaryAsync(
        string? userId,
        int driveSessionId,
        List<DistanceSample> samples,
        bool adverseConditions,
        int alertCount)
    {
        var baselines = new Dictionary<SpeedBand, double>();

        if (samples.Count == 0 || string.IsNullOrEmpty(userId))
            return baselines;

        // Group samples by band, excluding congested ticks
        var validSamples = samples.Where(s => !s.IsCongested).ToList();

        var byBand = validSamples
            .GroupBy(s => s.Band)
            .Where(g => g.Count() >= MinSamplesForBand);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var bandGroup in byBand)
        {
            var band = bandGroup.Key;
            var distances = bandGroup.Select(s => s.DistanceM).OrderBy(d => d).ToList();

            var avgDist   = distances.Average();
            var medianDist = Median(distances);
            var minDist   = distances.Min();

            // Get current baseline for this band before adding new row
            var currentBaseline = await GetPersonalBaselineAsync(userId, band);

            var entry = new FollowingDistanceHistory
            {
                UserId = userId,
                DriveSessionId = driveSessionId,
                RideEndedAt = DateTime.UtcNow,
                Band = band,
                AverageDistanceM = Math.Round(avgDist, 1),
                MedianDistanceM = Math.Round(medianDist, 1),
                MinDistanceM = Math.Round(minDist, 1),
                AlertCount = alertCount,
                SampleCount = bandGroup.Count(),
                BaselineAtTimeM = Math.Round(currentBaseline, 1),
                AdverseConditions = adverseConditions
            };

            db.FollowingDistanceHistory.Add(entry);
            await db.SaveChangesAsync();

            // Prune: keep only the 10 most recent per user per band
            var allForBand = await db.FollowingDistanceHistory
                .Where(h => h.UserId == userId && h.Band == band)
                .OrderByDescending(h => h.RideEndedAt)
                .ToListAsync();

            if (allForBand.Count > MaxHistoryPerBand)
            {
                var toDelete = allForBand.Skip(MaxHistoryPerBand).ToList();
                db.FollowingDistanceHistory.RemoveRange(toDelete);
                await db.SaveChangesAsync();
            }

            // Compute fresh baseline
            var newBaseline = await GetPersonalBaselineAsync(userId, band);
            baselines[band] = newBaseline;

            logger.LogInformation(
                "Following distance history saved: User={User} Band={Band} " +
                "Avg={Avg:F1}m Median={Med:F1}m Baseline={Base:F1}m Samples={N}",
                userId, band, avgDist, medianDist, newBaseline, bandGroup.Count());
        }

        return baselines;
    }

    // ── Get personal baseline ────────────────────────────────
    /// <summary>
    /// Returns the average of the last N ride averages for this user+band.
    /// Returns 0 if insufficient history (< 3 rides).
    /// </summary>
    public async Task<double> GetPersonalBaselineAsync(string userId, SpeedBand band)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var recentRides = await db.FollowingDistanceHistory
            .Where(h => h.UserId == userId && h.Band == band)
            .OrderByDescending(h => h.RideEndedAt)
            .Take(MaxHistoryPerBand)
            .Select(h => h.AverageDistanceM)
            .ToListAsync();

        if (recentRides.Count < 3)
            return 0; // Not enough data yet

        return recentRides.Average();
    }

    // ── Get all baselines for a session start ───────────────
    /// <summary>
    /// Loads baselines for all 5 bands at session start.
    /// Returns a dictionary to be cached in DriveSessionData.
    /// </summary>
    public async Task<Dictionary<SpeedBand, double>> GetAllBaselinesAsync(string userId)
    {
        var result = new Dictionary<SpeedBand, double>();
        foreach (SpeedBand band in Enum.GetValues<SpeedBand>())
        {
            result[band] = await GetPersonalBaselineAsync(userId, band);
        }
        return result;
    }

    // ── Get ride history for UI display ─────────────────────
    public async Task<List<FollowingDistanceHistory>> GetHistoryAsync(
        string userId, SpeedBand? band = null, int limit = 20)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = db.FollowingDistanceHistory
            .Where(h => h.UserId == userId);

        if (band.HasValue)
            query = query.Where(h => h.Band == band.Value);

        return await query
            .OrderByDescending(h => h.RideEndedAt)
            .Take(limit)
            .ToListAsync();
    }

    // ── Helpers ──────────────────────────────────────────────
    private static double Median(List<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
}
