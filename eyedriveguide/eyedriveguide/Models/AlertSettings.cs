using System.ComponentModel.DataAnnotations;

namespace EyeDriveGuide.Models
{
    public class AlertSettings
    {
        public int Id { get; set; }

        [Display(Name = "Yellow Alert Distance (m)")]
        [Range(1, 500)]
        public int YellowDistanceThreshold { get; set; } = 10;

        [Display(Name = "Red Alert Distance (m)")]
        [Range(1, 500)]
        public int RedDistanceThreshold { get; set; } = 15;

        [Display(Name = "Speed Alert Poll Interval (min)")]
        [Range(1, 60)]
        public int SpeedAlertPollIntervalMinutes { get; set; } = 2;

        [Display(Name = "Distraction dB Level")]
        [Range(30, 120)]
        public int DistractionDbLevel { get; set; } = 60;

        [Display(Name = "Passing-Lane Loiter Timeout (sec)")]
        [Range(10, 600)]
        public int PassingLaneLoiterSeconds { get; set; } = 60;

        [Display(Name = "Minimum Following Distance (m)")]
        [Range(5, 200)]
        public int FollowingDistanceMetres { get; set; } = 30;

        [Display(Name = "Auto-adjust for Weather / Night")]
        public bool WeatherNightModeAutoAdjust { get; set; } = false;
    }
}
