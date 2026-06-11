using EyeDriveGuide.Models;
using System.Collections.Concurrent;

namespace EyeDriveGuide.Services
{
    public class MergeAlertResult
    {
        public bool HasAlert { get; set; }
        public string? Message { get; set; }
        public string Severity { get; set; } = "info";
        public string AlertType { get; set; } = "merge";
    }

    public class MergeAlgorithm
    {
        private const double SpeedMatchThresholdKmh = 15;
        private const double Alert600M = 600;
        private const double Alert300M = 300;
        private const double AtMerge = 80;

        private readonly ConcurrentDictionary<string, bool> _firedAlerts = new();

        public MergeAlertResult? Evaluate(
            GeoCoordinate position,
            double speedKmh,
            RouteEvent mergeEvent,
            double? accelMagnitude,
            bool alreadyMerged,
            string sessionId)
        {
            if (alreadyMerged) return null;

            var distToMerge = position.DistanceTo(mergeEvent.Coord);
            var segmentSpeedLimit = 100.0;

            var key = $"{sessionId}:{mergeEvent.Coord.Lat:F4},{mergeEvent.Coord.Lng:F4}";

            if (distToMerge > 1200) return null;

            if (distToMerge <= AtMerge)
            {
                return Alert(key, "merge-now", "Merge now — move into highway traffic", "danger", "merge");
            }

            if (distToMerge <= Alert300M)
            {
                if (accelMagnitude.HasValue && accelMagnitude.Value > 12)
                    return Alert(key, "hold-gap", "Hold — wait for gap in traffic", "warning", "merge");

                if (speedKmh < segmentSpeedLimit - SpeedMatchThresholdKmh)
                    return Alert(key, "accel-match", $"Accelerate to match highway speed ({segmentSpeedLimit:0} km/h)", "warning", "merge");

                return Alert(key, "signal-now", "Signal now — prepare to merge", "warning", "merge");
            }

            if (distToMerge <= Alert600M)
            {
                return Alert(key, "check-mirrors", "Check mirrors — merge in 600 m", "info", "merge");
            }

            if (distToMerge <= 1200 && speedKmh < segmentSpeedLimit - SpeedMatchThresholdKmh)
            {
                return Alert(key, "speed-up-ramp", $"Accelerate on ramp — highway speed {segmentSpeedLimit:0} km/h", "info", "merge");
            }

            return null;
        }

        public MergeAlertResult PostMergeAlert(string sessionId)
        {
            return new MergeAlertResult
            {
                HasAlert = true,
                Message = "Move to travel lane when safe",
                Severity = "info",
                AlertType = "merge"
            };
        }

        private MergeAlertResult? Alert(string baseKey, string subKey, string message, string severity, string alertType)
        {
            var key = $"{baseKey}:{subKey}";
            if (!_firedAlerts.TryAdd(key, true)) return null;
            return new MergeAlertResult
            {
                HasAlert = true,
                Message = message,
                Severity = severity,
                AlertType = alertType
            };
        }

        public void ResetSession(string sessionId)
        {
            foreach (var key in _firedAlerts.Keys.Where(k => k.StartsWith(sessionId)).ToList())
                _firedAlerts.TryRemove(key, out _);
        }
    }
}
