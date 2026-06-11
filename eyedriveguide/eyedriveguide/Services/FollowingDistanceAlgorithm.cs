// ============================================================
// FollowingDistanceAlgorithm.cs
// Core evaluation engine for the adaptive following distance monitor.
//
// Responsibilities:
//   1. Classify current speed into a SpeedBand
//   2. Look up physics-derived thresholds from AlertSettings
//   3. Detect traffic jam / signal stop → suppress alerts
//   4. Compare distance to: min-safe, warn, ideal, personal baseline
//   5. Apply cooldowns so the driver isn't spammed
//   6. Collect samples for post-ride 10-ride history
// ============================================================
using EyeDriveGuide.Models;
using System.Collections.Concurrent;

namespace EyeDriveGuide.Services;

// ── Result types ────────────────────────────────────────────

public enum FollowingAlertLevel
{
    None,
    Advisory,       // Within warn band — gentle reminder
    Warning,        // Below warn, above min — actionable
    Critical,       // Below min — danger
    PersonalDrift   // Below driver's own 10-ride baseline
}

public class FollowingDistanceAlert
{
    public bool HasAlert { get; set; }
    public FollowingAlertLevel Level { get; set; }
    public string? Message { get; set; }
    public string Severity { get; set; } = "info";
    public string AlertType { get; set; } = "following";
    public double CurrentDistanceM { get; set; }
    public double RecommendedDistanceM { get; set; }
    public SpeedBand Band { get; set; }
    public bool Suppressed { get; set; }        // True when jam/signal detected
    public string? SuppressReason { get; set; }
}

// ── Per-session state ────────────────────────────────────────

public class FollowingDistanceSessionState
{
    // Recent speed readings for trend analysis (circular buffer, last 6 ticks ≈ 3 s)
    public Queue<double> SpeedHistory { get; } = new();
    private const int SpeedHistorySize = 6;

    // Cooldown tracking
    public DateTime? LastAdvisoryAt { get; set; }
    public DateTime? LastWarningAt { get; set; }
    public DateTime? LastCriticalAt { get; set; }
    public DateTime? LastBaselineDriftAt { get; set; }

    // Sample buffer for post-ride summary
    public List<DistanceSample> Samples { get; } = new();

    // Alert count for this session
    public int AlertCount { get; set; }

    // Traffic pattern detection
    public int ConsecutiveDeceleratingTicks { get; set; }
    public int StopGoPatternCount { get; set; }   // How many 0→stop→go cycles detected
    public double? PreviousSpeed { get; set; }

    public void AddSpeedSample(double speed)
    {
        SpeedHistory.Enqueue(speed);
        if (SpeedHistory.Count > SpeedHistorySize)
            SpeedHistory.Dequeue();
    }

    /// <summary>True if speed trend is consistently decelerating.</summary>
    public bool IsDecelerating()
    {
        if (SpeedHistory.Count < 3) return false;
        var arr = SpeedHistory.ToArray();
        int decelCount = 0;
        for (int i = 1; i < arr.Length; i++)
            if (arr[i] < arr[i - 1] - 1.0) decelCount++;
        return decelCount >= arr.Length - 2;
    }

    /// <summary>True if recent speed pattern looks like traffic light cycling.</summary>
    public bool IsSignalPattern()
    {
        // A stop-and-go pattern: at least 2 near-zero speed readings in the last 6
        var zeroCount = SpeedHistory.Count(s => s < 3.0);
        return zeroCount >= 2;
    }
}

// ── Main algorithm ───────────────────────────────────────────

public class FollowingDistanceAlgorithm
{
    private readonly ConcurrentDictionary<string, FollowingDistanceSessionState> _states = new();
    private readonly ILogger<FollowingDistanceAlgorithm> _logger;

    // Night/wet road multiplier — applied when conditions are adverse
    private const double AdverseConditionMultiplier = 1.30;

    public FollowingDistanceAlgorithm(ILogger<FollowingDistanceAlgorithm> logger)
    {
        _logger = logger;
    }

    public FollowingDistanceSessionState GetState(string sessionId) =>
        _states.GetOrAdd(sessionId, _ => new FollowingDistanceSessionState());

    // ── Main evaluation entry point ──────────────────────────
    // Called from DriveHub.UpdatePosition on every GPS tick.
    // frontDistanceM: distance to vehicle ahead in metres (null = sensor not available)
    // personalBaseline: driver's 10-ride average for this band (null = insufficient history)
    public FollowingDistanceAlert Evaluate(
        string sessionId,
        double speedKmh,
        double? frontDistanceM,
        double? personalBaseline,
        AlertSettings settings,
        bool nightMode,
        bool adverseWeather)
    {
        var state = GetState(sessionId);
        state.AddSpeedSample(speedKmh);

        var noAlert = new FollowingDistanceAlert { HasAlert = false };

        // No sensor data → nothing to evaluate
        if (!frontDistanceM.HasValue || frontDistanceM.Value <= 0)
        {
            state.PreviousSpeed = speedKmh;
            return noAlert;
        }

        if (!settings.FollowingDistanceAlertsEnabled)
            return noAlert;

        var distM = frontDistanceM.Value;
        var band = ClassifySpeedBand(speedKmh);
        var thresholds = GetThresholds(band, settings);

        // Apply adverse condition multiplier
        var conditionMultiplier = (nightMode || adverseWeather) && settings.WeatherNightModeAutoAdjust
            ? (settings.NightWetRoadMultiplierPct / 100.0)
            : 1.0;
        var adjustedMin  = thresholds.MinM  * conditionMultiplier;
        var adjustedWarn = thresholds.WarnM * conditionMultiplier;
        var adjustedIdeal = thresholds.IdealM * conditionMultiplier;

        // Track deceleration pattern
        if (state.PreviousSpeed.HasValue && speedKmh < state.PreviousSpeed.Value - 2.0)
            state.ConsecutiveDeceleratingTicks++;
        else
            state.ConsecutiveDeceleratingTicks = 0;

        state.PreviousSpeed = speedKmh;

        // ── Record sample (even suppressed ones, for post-ride stats) ──
        bool isCongested = IsCongestedOrStopped(speedKmh, distM, state, settings);
        state.Samples.Add(new DistanceSample(
            DateTime.UtcNow, speedKmh, distM, band, isCongested));

        // ── Suppress in jams / at signals ───────────────────
        if (isCongested)
        {
            return new FollowingDistanceAlert
            {
                HasAlert = false,
                Suppressed = true,
                SuppressReason = speedKmh < settings.StoppedSpeedThresholdKmh
                    ? "stopped-signal"
                    : "traffic-jam",
                CurrentDistanceM = distM,
                Band = band
            };
        }

        // ── Vehicle not moving fast enough to need distance alert ──
        // At very low speed, even a short distance is fine (parking lot, etc.)
        if (speedKmh < 10)
            return noAlert;

        var now = DateTime.UtcNow;
        FollowingDistanceAlert? alert = null;

        // ── CRITICAL: Below minimum safe distance ───────────
        if (distM <= adjustedMin && speedKmh > 30)
        {
            var cooldown = TimeSpan.FromSeconds(5); // Short cooldown — this is danger
            if (!state.LastCriticalAt.HasValue || (now - state.LastCriticalAt.Value) >= cooldown)
            {
                state.LastCriticalAt = now;
                state.AlertCount++;
                alert = new FollowingDistanceAlert
                {
                    HasAlert = true,
                    Level = FollowingAlertLevel.Critical,
                    Message = $"⚠️ Too close — ease off! Safe gap: {adjustedWarn:0} m",
                    Severity = "danger",
                    AlertType = "following",
                    CurrentDistanceM = distM,
                    RecommendedDistanceM = adjustedIdeal,
                    Band = band
                };
            }
        }
        // ── WARNING: Below warn threshold ───────────────────
        else if (distM <= adjustedWarn && distM > adjustedMin)
        {
            var cooldown = TimeSpan.FromSeconds(settings.FollowingWarningCooldownSec);
            if (!state.LastWarningAt.HasValue || (now - state.LastWarningAt.Value) >= cooldown)
            {
                state.LastWarningAt = now;
                state.AlertCount++;
                alert = new FollowingDistanceAlert
                {
                    HasAlert = true,
                    Level = FollowingAlertLevel.Warning,
                    Message = $"Ease back — aim for {adjustedIdeal:0} m at {speedKmh:0} km/h",
                    Severity = "warning",
                    AlertType = "following",
                    CurrentDistanceM = distM,
                    RecommendedDistanceM = adjustedIdeal,
                    Band = band
                };
            }
        }
        // ── ADVISORY: Between warn and ideal ────────────────
        else if (distM > adjustedWarn && distM < adjustedIdeal)
        {
            var cooldown = TimeSpan.FromSeconds(settings.FollowingAdvisoryCooldownSec);
            if (!state.LastAdvisoryAt.HasValue || (now - state.LastAdvisoryAt.Value) >= cooldown)
            {
                state.LastAdvisoryAt = now;
                alert = new FollowingDistanceAlert
                {
                    HasAlert = true,
                    Level = FollowingAlertLevel.Advisory,
                    Message = $"Maintain following distance — {adjustedIdeal:0} m recommended",
                    Severity = "info",
                    AlertType = "following",
                    CurrentDistanceM = distM,
                    RecommendedDistanceM = adjustedIdeal,
                    Band = band
                };
            }
        }

        // ── PERSONAL BASELINE DRIFT (runs in addition to above) ──
        if (settings.PersonalBaselineAlertEnabled &&
            personalBaseline.HasValue &&
            personalBaseline.Value > 0 &&
            distM < (personalBaseline.Value * settings.BaselineDriftThresholdPct / 100.0) &&
            distM > adjustedWarn) // Only fire if not already in warning/critical
        {
            var cooldown = TimeSpan.FromSeconds(settings.FollowingBaselineCooldownSec);
            if (!state.LastBaselineDriftAt.HasValue || (now - state.LastBaselineDriftAt.Value) >= cooldown)
            {
                state.LastBaselineDriftAt = now;
                // Only override if no higher-severity alert is already firing
                if (alert == null)
                {
                    alert = new FollowingDistanceAlert
                    {
                        HasAlert = true,
                        Level = FollowingAlertLevel.PersonalDrift,
                        Message = $"Closer than your usual gap — you typically keep {personalBaseline.Value:0} m here",
                        Severity = "info",
                        AlertType = "following",
                        CurrentDistanceM = distM,
                        RecommendedDistanceM = personalBaseline.Value,
                        Band = band
                    };
                }
            }
        }

        return alert ?? new FollowingDistanceAlert
        {
            HasAlert = false,
            CurrentDistanceM = distM,
            Band = band
        };
    }

    // ── Speed band classifier ────────────────────────────────
    public static SpeedBand ClassifySpeedBand(double speedKmh) => speedKmh switch
    {
        <= 25 => SpeedBand.UrbanLow,
        <= 45 => SpeedBand.UrbanMid,
        <= 55 => SpeedBand.Suburban,
        <= 65 => SpeedBand.Highway,
        _     => SpeedBand.Freeway
    };

    // ── Threshold lookup ─────────────────────────────────────
    private static (double MinM, double WarnM, double IdealM) GetThresholds(
        SpeedBand band, AlertSettings s) => band switch
    {
        SpeedBand.UrbanLow  => (s.UrbanLowMinM,  s.UrbanLowWarnM,  s.UrbanLowIdealM),
        SpeedBand.UrbanMid  => (s.UrbanMidMinM,  s.UrbanMidWarnM,  s.UrbanMidIdealM),
        SpeedBand.Suburban  => (s.SuburbanMinM,   s.SuburbanWarnM,  s.SuburbanIdealM),
        SpeedBand.Highway   => (s.HighwayMinM,    s.HighwayWarnM,   s.HighwayIdealM),
        SpeedBand.Freeway   => (s.FreewayMinM,    s.FreewayWarnM,   s.FreewayIdealM),
        _                   => (s.HighwayMinM,    s.HighwayWarnM,   s.HighwayIdealM)
    };

    // ── Traffic jam / signal detection ──────────────────────
    private static bool IsCongestedOrStopped(
        double speedKmh,
        double distM,
        FollowingDistanceSessionState state,
        AlertSettings settings)
    {
        // Hard-stopped at a signal
        if (speedKmh < settings.StoppedSpeedThresholdKmh)
            return true;

        // Traffic jam: slow + close + decelerating
        if (speedKmh < settings.TrafficJamSpeedThresholdKmh &&
            distM < 8.0 &&
            state.ConsecutiveDeceleratingTicks >= 3)
            return true;

        // Stop-and-go traffic light cycling pattern
        if (speedKmh < settings.TrafficJamSpeedThresholdKmh &&
            state.IsSignalPattern())
            return true;

        return false;
    }

    // ── Session cleanup ──────────────────────────────────────
    public FollowingDistanceSessionState? ExtractAndReset(string sessionId)
    {
        _states.TryRemove(sessionId, out var state);
        return state;
    }

    public void ResetSession(string sessionId) =>
        _states.TryRemove(sessionId, out _);
}
