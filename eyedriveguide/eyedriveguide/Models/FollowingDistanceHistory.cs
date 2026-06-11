// ============================================================
// FollowingDistanceHistory.cs
// EF Core entity storing per-ride following distance summaries.
// Max 10 rows per user per speed band — oldest auto-purged.
// ============================================================
namespace EyeDriveGuide.Models;

/// <summary>Speed bands matching AlertSettings thresholds.</summary>
public enum SpeedBand
{
    UrbanLow   = 0,  // 0–25 km/h
    UrbanMid   = 1,  // 26–45 km/h
    Suburban   = 2,  // 46–55 km/h
    Highway    = 3,  // 56–65 km/h
    Freeway    = 4   // 66+ km/h
}

/// <summary>
/// One row per completed highway-eligible ride, per speed band.
/// Stores the data needed to compute the 10-ride rolling baseline.
/// </summary>
public class FollowingDistanceHistory
{
    public int Id { get; set; }

    /// <summary>User identity — for multi-user scoping.</summary>
    public string? UserId { get; set; }

    /// <summary>DriveSession.Id this summary was computed from.</summary>
    public int DriveSessionId { get; set; }

    /// <summary>When the ride ended.</summary>
    public DateTime RideEndedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Dominant speed band for this ride.</summary>
    public SpeedBand Band { get; set; }

    /// <summary>Mean distance maintained to the vehicle ahead (metres).</summary>
    public double AverageDistanceM { get; set; }

    /// <summary>Median distance — less sensitive to traffic jam outliers.</summary>
    public double MedianDistanceM { get; set; }

    /// <summary>Minimum distance recorded during the ride (metres).</summary>
    public double MinDistanceM { get; set; }

    /// <summary>Number of following-distance alerts fired during the ride.</summary>
    public int AlertCount { get; set; }

    /// <summary>Number of distance samples collected.</summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// Computed personalised baseline at the time this ride was saved.
    /// Stored so we can show the driver their trend over time.
    /// </summary>
    public double BaselineAtTimeM { get; set; }

    /// <summary>True if night-mode or wet-road conditions were active.</summary>
    public bool AdverseConditions { get; set; }
}

/// <summary>
/// In-memory sample collected during a live drive tick.
/// Not persisted to DB — only the per-ride summary is stored.
/// </summary>
public record DistanceSample(
    DateTime Timestamp,
    double SpeedKmh,
    double DistanceM,
    SpeedBand Band,
    bool IsCongested
);
