namespace EyeDriveGuide.Models
{
    public class DriveSession
    {
        public int Id { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? EndedAt { get; set; }
        public double TotalDistanceKm { get; set; }
        public double AverageSpeedKmh { get; set; }
        public double SpeedConsistencyScore { get; set; }
        public int SpeedAlertCount { get; set; }
        public int DistractionAlertCount { get; set; }
        public int BackingAlertCount { get; set; }
        public int LaneChangeAlertCount { get; set; }
        public int MergeAlertCount { get; set; }
        public int ExitAlertCount { get; set; }
        public string? DestinationAddress { get; set; }
        public string Mode { get; set; } = "JustDrive";
    }
}
