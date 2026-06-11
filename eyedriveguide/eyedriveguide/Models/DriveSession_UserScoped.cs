// ============================================================
// DriveSession.cs — SECURITY FIX OW-1 / OW-8
// Adds UserId for row-level security.
// DestinationAddress encrypted at rest via AppDbContext.
// ============================================================
namespace EyeDriveGuide.Models;

public class DriveSession
{
    public int Id { get; set; }

    // SECURITY FIX OW-1: owner identifier
    public string? UserId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    // SECURITY FIX DS-2: encrypted at rest via EncryptedStringConverter
    public string? DestinationAddress { get; set; }

    public string Mode { get; set; } = "JustDrive";

    // SECURITY FIX OW-8: all metrics are server-authoritative.
    // These are NEVER accepted from the client body.
    // They are populated exclusively by the SignalR hub's EndSession().
    public double TotalDistanceKm { get; set; }
    public double AverageSpeedKmh { get; set; }
    public double SpeedConsistencyScore { get; set; }
    public int SpeedAlertCount { get; set; }
    public int DistractionAlertCount { get; set; }
    public int BackingAlertCount { get; set; }
    public int LaneChangeAlertCount { get; set; }
    public int MergeAlertCount { get; set; }
    public int ExitAlertCount { get; set; }
}
