// ============================================================
// DriveSessionData — Following Distance additions
// PATCH: Add these fields to the existing DriveSessionData class.
// DriveSessionData lives in Hubs/DriveHub.cs or a separate file.
// ============================================================
namespace EyeDriveGuide.Models;

// Add these fields to DriveSessionData:
public partial class DriveSessionData
{
    // ── Following distance tracking ──────────────────────────

    /// <summary>
    /// Raw distance samples collected during this session.
    /// Kept in memory only; summarised to DB on EndSession.
    /// </summary>
    public List<DistanceSample> FollowingDistanceSamples { get; set; } = new();

    /// <summary>
    /// Personal baselines loaded at session start (band → metres).
    /// Null entries = insufficient history for that band.
    /// </summary>
    public Dictionary<SpeedBand, double>? PersonalBaselines { get; set; }

    /// <summary>Total following-distance alerts fired in this session.</summary>
    public int FollowingDistanceAlertCount { get; set; }

    /// <summary>Adverse weather active (wet road, fog, heavy rain).</summary>
    public bool AdverseWeather { get; set; }
}
