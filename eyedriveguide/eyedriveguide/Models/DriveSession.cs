// ============================================================
// DriveSession.cs — Security-Updated (merged, single definition)
// REPLACES both DriveSession.cs and DriveSession_UserScoped.cs
// SECURITY FIXES:
//   OW-1 — UserId for row-level security
//   OW-8 — all metrics server-authoritative (never client-supplied)
//   DS-2 — DestinationAddress encrypted via AppDbContext
// ============================================================
namespace EyeDriveGuide.Models;

public class DriveSession
{
    public int Id { get; set; }

    // SECURITY FIX OW-1: owner identifier for user-scoped queries
    public string? UserId { get; set; }

    public DateTime  StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt   { get; set; }
    public string    Mode      { get; set; } = "JustDrive";

    // SECURITY FIX DS-2: encrypted at rest via EncryptedStringConverter in AppDbContext
    public string? DestinationAddress { get; set; }

    // SECURITY FIX OW-8: all metrics are server-authoritative.
    // Never accepted from client body — populated only by SignalR hub EndSession().
    public double TotalDistanceKm        { get; set; }
    public double AverageSpeedKmh        { get; set; }
    public double SpeedConsistencyScore  { get; set; }
    public int    SpeedAlertCount        { get; set; }
    public int    DistractionAlertCount  { get; set; }
    public int    BackingAlertCount      { get; set; }
    public int    LaneChangeAlertCount   { get; set; }
    public int    MergeAlertCount        { get; set; }
    public int    ExitAlertCount         { get; set; }
}
