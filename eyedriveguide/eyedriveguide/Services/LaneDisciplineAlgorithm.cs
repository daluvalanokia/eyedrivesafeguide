using EyeDriveGuide.Models;
using System.Collections.Concurrent;

namespace EyeDriveGuide.Services
{
    public class LaneDisciplineAlert
    {
        public bool HasAlert { get; set; }
        public string? Message { get; set; }
        public string Severity { get; set; } = "info";
        public string AlertType { get; set; } = "lane";
        public bool ShowBlindSpotHold { get; set; }
    }

    public class SessionLaneState
    {
        public int CurrentLane { get; set; } = 0;
        public DateTime? InLeftLaneSince { get; set; }
        public bool YieldAlertFired { get; set; }
        public bool ReturnRightAlertFired { get; set; }
        public DateTime? OnRampYieldFiredAt { get; set; }
        public DateTime? SlowSpeedSince { get; set; }
        public bool PassingAlertFired { get; set; }
    }

    public class LaneDisciplineAlgorithm
    {
        private readonly ConcurrentDictionary<string, SessionLaneState> _states = new();

        public SessionLaneState GetState(string sessionId)
        {
            return _states.GetOrAdd(sessionId, _ => new SessionLaneState());
        }

        public LaneDisciplineAlert? Evaluate(
            string sessionId,
            GeoCoordinate position,
            double speedKmh,
            double speedLimitKmh,
            int laneCount,
            bool isHighway,
            double? accelMagnitude,
            RouteGraph route,
            int loiterTimeoutSeconds)
        {
            if (!isHighway || laneCount < 2) return null;

            var state = GetState(sessionId);

            if (accelMagnitude.HasValue && accelMagnitude.Value > 14)
            {
                return new LaneDisciplineAlert
                {
                    HasAlert = true,
                    Message = "Traffic slowing — check lanes",
                    Severity = "warning",
                    AlertType = "lane",
                    ShowBlindSpotHold = false
                };
            }

            var upcomingOnRamp = route.Events
                .Where(e => e.Type == RouteEventType.OnRamp)
                .FirstOrDefault(e => {
                    var d = position.DistanceTo(e.Coord);
                    return d > 0 && d <= 800;
                });

            if (upcomingOnRamp != null)
            {
                if (!state.YieldAlertFired)
                {
                    state.YieldAlertFired = true;
                    state.ReturnRightAlertFired = false;
                    state.OnRampYieldFiredAt = DateTime.UtcNow;
                    return new LaneDisciplineAlert
                    {
                        HasAlert = true,
                        Message = "Move left — merging traffic ahead",
                        Severity = "info",
                        AlertType = "lane",
                        ShowBlindSpotHold = true
                    };
                }
            }
            else if (state.YieldAlertFired && !state.ReturnRightAlertFired &&
                     state.OnRampYieldFiredAt.HasValue &&
                     (DateTime.UtcNow - state.OnRampYieldFiredAt.Value).TotalSeconds > 20)
            {
                state.ReturnRightAlertFired = true;
                state.YieldAlertFired = false;
                return new LaneDisciplineAlert
                {
                    HasAlert = true,
                    Message = "On-ramp passed — move back right when safe",
                    Severity = "info",
                    AlertType = "lane",
                    ShowBlindSpotHold = true
                };
            }

            if (state.CurrentLane == 0 && laneCount >= 2 && speedKmh <= speedLimitKmh)
            {
                if (state.InLeftLaneSince == null)
                {
                    state.InLeftLaneSince = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - state.InLeftLaneSince.Value).TotalSeconds >= loiterTimeoutSeconds)
                {
                    state.InLeftLaneSince = DateTime.UtcNow;
                    return new LaneDisciplineAlert
                    {
                        HasAlert = true,
                        Message = "Move right — you're in the passing lane",
                        Severity = "warning",
                        AlertType = "lane",
                        ShowBlindSpotHold = true
                    };
                }
            }
            else
            {
                state.InLeftLaneSince = null;
            }

            if (state.CurrentLane > 0 && laneCount >= 2 && speedKmh > 0 && speedKmh < speedLimitKmh - 20)
            {
                if (state.SlowSpeedSince == null)
                {
                    state.SlowSpeedSince = DateTime.UtcNow;
                    state.PassingAlertFired = false;
                }
                else if (!state.PassingAlertFired &&
                         (DateTime.UtcNow - state.SlowSpeedSince.Value).TotalSeconds >= 15)
                {
                    state.PassingAlertFired = true;
                    return new LaneDisciplineAlert
                    {
                        HasAlert = true,
                        Message = "Slow vehicle ahead — consider passing on left if safe",
                        Severity = "info",
                        AlertType = "lane",
                        ShowBlindSpotHold = false
                    };
                }
            }
            else
            {
                state.SlowSpeedSince = null;
                state.PassingAlertFired = false;
            }

            return null;
        }

        public void UpdateLane(string sessionId, int laneIndex)
        {
            var state = GetState(sessionId);
            if (state.CurrentLane != laneIndex)
            {
                state.CurrentLane = laneIndex;
                state.InLeftLaneSince = laneIndex == 0 ? DateTime.UtcNow : null;
            }
        }

        public void ResetSession(string sessionId)
        {
            _states.TryRemove(sessionId, out _);
        }
    }
}
