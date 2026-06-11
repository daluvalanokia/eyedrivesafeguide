using EyeDriveGuide.Models;
using System.Collections.Concurrent;

namespace EyeDriveGuide.Services
{
    public class ExitAlert
    {
        public bool HasAlert { get; set; }
        public string? Message { get; set; }
        public string Severity { get; set; } = "info";
        public string AlertType { get; set; } = "exit";
        public bool ShowBlindSpotHold { get; set; }
        public bool MissedExit { get; set; }
    }

    public class SessionExitState
    {
        public string? LastFiredStage { get; set; }
        public DateTime? LastLaneStaggerAt { get; set; }
        public int StaggersCompleted { get; set; }
        public bool ExitTaken { get; set; }
        public bool MissedAlertFired { get; set; }
        public bool HasApproached { get; set; }
        public double ApproachMinDist { get; set; } = double.MaxValue;
    }

    public class ExitAlgorithm
    {
        private readonly ConcurrentDictionary<string, SessionExitState> _states = new();

        public ExitAlert? Evaluate(
            string sessionId,
            GeoCoordinate position,
            double speedKmh,
            RouteEvent exitEvent,
            int currentLane,
            int totalLanes)
        {
            var state = GetState(sessionId, exitEvent);

            if (state.ExitTaken) return null;

            var distToExit = position.DistanceTo(exitEvent.Coord);
            var exitLane = exitEvent.ExitLaneIndex ?? (totalLanes - 1);
            var lanesFromExit = Math.Abs(currentLane - exitLane);

            if (distToExit < 200)
            {
                state.HasApproached = true;
                state.ApproachMinDist = Math.Min(state.ApproachMinDist, distToExit);
            }

            if (distToExit < 50)
            {
                state.ExitTaken = true;
                return new ExitAlert
                {
                    HasAlert = true,
                    Message = "Take exit now",
                    Severity = "danger",
                    AlertType = "exit"
                };
            }

            if (exitEvent.AdvisoryRampSpeedKmh.HasValue &&
                distToExit < 500 &&
                speedKmh > exitEvent.AdvisoryRampSpeedKmh.Value)
            {
                return new ExitAlert
                {
                    HasAlert = true,
                    Message = $"Slow down — exit ramp speed {exitEvent.AdvisoryRampSpeedKmh.Value:0} km/h",
                    Severity = "warning",
                    AlertType = "exit"
                };
            }

            if (state.HasApproached && distToExit > 500 && !state.MissedAlertFired && !state.ExitTaken)
            {
                state.MissedAlertFired = true;
                return new ExitAlert
                {
                    HasAlert = true,
                    Message = "Missed exit — recalculating route",
                    Severity = "danger",
                    AlertType = "exit",
                    MissedExit = true
                };
            }

            if (lanesFromExit >= 2)
            {
                var now = DateTime.UtcNow;
                var canStagger = state.LastLaneStaggerAt == null ||
                                 (now - state.LastLaneStaggerAt.Value).TotalSeconds >= 30;

                if (distToExit <= 3220 && canStagger)
                {
                    state.LastLaneStaggerAt = now;
                    state.StaggersCompleted++;
                    return new ExitAlert
                    {
                        HasAlert = true,
                        Message = "Move right one lane — exit ahead",
                        Severity = "warning",
                        AlertType = "exit",
                        ShowBlindSpotHold = true
                    };
                }
                return null;
            }

            string? stage = null;
            string? msg = null;
            string sev = "info";

            if (distToExit <= 3220 && state.LastFiredStage != "2mi")
            {
                stage = "2mi";
                msg = "Your exit is in 2 miles — begin moving right";
                sev = "info";
            }
            else if (distToExit <= 1610 && state.LastFiredStage != "1mi" && state.LastFiredStage == "2mi")
            {
                stage = "1mi";
                msg = "Move to right lane now — exit in 1 mile";
                sev = "warning";
            }
            else if (distToExit <= 800 && state.LastFiredStage == "1mi")
            {
                stage = "500m";
                msg = "Exit in 500 m — signal right";
                sev = "warning";
            }

            if (stage != null && msg != null)
            {
                state.LastFiredStage = stage;
                return new ExitAlert
                {
                    HasAlert = true,
                    Message = msg,
                    Severity = sev,
                    AlertType = "exit",
                    ShowBlindSpotHold = stage != "2mi"
                };
            }

            return null;
        }

        private SessionExitState GetState(string sessionId, RouteEvent exitEvent)
        {
            var key = $"{sessionId}:{exitEvent.Coord.Lat:F4},{exitEvent.Coord.Lng:F4}";
            return _states.GetOrAdd(key, _ => new SessionExitState());
        }

        public void ResetSession(string sessionId)
        {
            foreach (var key in _states.Keys.Where(k => k.StartsWith(sessionId)).ToList())
                _states.TryRemove(key, out _);
        }
    }
}
