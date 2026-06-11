// ============================================================
// AlertSettings.cs — Merged (single definition)
// REPLACES both AlertSettings.cs and AlertSettings_Updated.cs
// Adds all 5-band following distance config + alert behaviour.
// ============================================================
using System.ComponentModel.DataAnnotations;

namespace EyeDriveGuide.Models;

public class AlertSettings
{
    public int Id { get; set; }

    // ── Existing fields ──────────────────────────────────────
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

    [Display(Name = "Auto-adjust for Weather / Night")]
    public bool WeatherNightModeAutoAdjust { get; set; } = false;

    // ── Following Distance: per-band thresholds ─────────────
    // Band 1 — Urban Low (0–25 km/h)
    [Display(Name = "Urban Low: Ideal Distance (m)")]  [Range(5, 100)]  public int UrbanLowIdealM  { get; set; } = 18;
    [Display(Name = "Urban Low: Warn Distance (m)")]   [Range(3, 80)]   public int UrbanLowWarnM   { get; set; } = 12;
    [Display(Name = "Urban Low: Min Safe (m)")]        [Range(2, 50)]   public int UrbanLowMinM    { get; set; } = 7;

    // Band 2 — Urban Mid (26–45 km/h)
    [Display(Name = "Urban Mid: Ideal Distance (m)")]  [Range(10, 150)] public int UrbanMidIdealM  { get; set; } = 30;
    [Display(Name = "Urban Mid: Warn Distance (m)")]   [Range(8, 100)]  public int UrbanMidWarnM   { get; set; } = 22;
    [Display(Name = "Urban Mid: Min Safe (m)")]        [Range(5, 60)]   public int UrbanMidMinM    { get; set; } = 14;

    // Band 3 — Suburban (46–55 km/h)
    [Display(Name = "Suburban: Ideal Distance (m)")]   [Range(15, 200)] public int SuburbanIdealM  { get; set; } = 45;
    [Display(Name = "Suburban: Warn Distance (m)")]    [Range(10, 150)] public int SuburbanWarnM   { get; set; } = 32;
    [Display(Name = "Suburban: Min Safe (m)")]         [Range(8, 80)]   public int SuburbanMinM    { get; set; } = 20;

    // Band 4 — Highway (56–65 km/h)
    [Display(Name = "Highway: Ideal Distance (m)")]    [Range(20, 250)] public int HighwayIdealM   { get; set; } = 60;
    [Display(Name = "Highway: Warn Distance (m)")]     [Range(15, 180)] public int HighwayWarnM    { get; set; } = 44;
    [Display(Name = "Highway: Min Safe (m)")]          [Range(10, 100)] public int HighwayMinM     { get; set; } = 28;

    // Band 5 — Freeway (66+ km/h)
    [Display(Name = "Freeway: Ideal Distance (m)")]    [Range(25, 300)] public int FreewayIdealM   { get; set; } = 75;
    [Display(Name = "Freeway: Warn Distance (m)")]     [Range(20, 220)] public int FreewayWarnM    { get; set; } = 55;
    [Display(Name = "Freeway: Min Safe (m)")]          [Range(15, 120)] public int FreewayMinM     { get; set; } = 35;

    // ── Following Distance: alert behaviour ─────────────────
    [Display(Name = "Following Distance Alerts Enabled")]
    public bool FollowingDistanceAlertsEnabled { get; set; } = true;

    [Display(Name = "Advisory Cooldown (sec)")]        [Range(30, 600)] public int FollowingAdvisoryCooldownSec  { get; set; } = 90;
    [Display(Name = "Warning Cooldown (sec)")]         [Range(10, 300)] public int FollowingWarningCooldownSec   { get; set; } = 30;
    [Display(Name = "Baseline Drift Cooldown (sec)")]  [Range(60, 600)] public int FollowingBaselineCooldownSec  { get; set; } = 120;

    [Display(Name = "Personal Baseline Alert Enabled")]
    public bool PersonalBaselineAlertEnabled { get; set; } = true;

    [Display(Name = "Baseline Drift Threshold (%)")]   [Range(50, 95)]  public int BaselineDriftThresholdPct      { get; set; } = 80;
    [Display(Name = "Traffic Jam Speed Threshold (km/h)")] [Range(5, 30)] public int TrafficJamSpeedThresholdKmh  { get; set; } = 15;
    [Display(Name = "Stopped Signal Speed Threshold (km/h)")] [Range(1, 10)] public int StoppedSpeedThresholdKmh { get; set; } = 5;
    [Display(Name = "Night/Wet Road Multiplier (%)")]  [Range(100, 200)] public int NightWetRoadMultiplierPct     { get; set; } = 130;

    // ── Legacy field — kept for EF migration compatibility ──
    [Display(Name = "Minimum Following Distance (m) [legacy]")]
    [Range(5, 200)]
    public int FollowingDistanceMetres { get; set; } = 30;
}
