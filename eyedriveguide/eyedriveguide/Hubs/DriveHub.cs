using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using EyeDriveGuide.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EyeDriveGuide.Hubs
{
    public class DriveHub : Hub
    {
        private readonly IMemoryCache _cache;
        private readonly MergeAlgorithm _merge;
        private readonly LaneDisciplineAlgorithm _lane;
        private readonly ExitAlgorithm _exit;
        private readonly IServiceScopeFactory _scopeFactory;

        public DriveHub(
            IMemoryCache cache,
            MergeAlgorithm merge,
            LaneDisciplineAlgorithm lane,
            ExitAlgorithm exit,
            IServiceScopeFactory scopeFactory)
        {
            _cache = cache;
            _merge = merge;
            _lane = lane;
            _exit = exit;
            _scopeFactory = scopeFactory;
        }

        public async Task StartSession(string mode, string? destinationAddress)
        {
            var sessionId = Context.ConnectionId;
            var sessionData = new DriveSessionData
            {
                SessionId = sessionId,
                Mode = mode,
                DestinationAddress = destinationAddress,
                StartedAt = DateTime.UtcNow
            };
            _cache.Set($"session:{sessionId}", sessionData, TimeSpan.FromHours(12));

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbSession = new DriveSession
            {
                StartedAt = DateTime.UtcNow,
                Mode = mode,
                DestinationAddress = destinationAddress
            };
            db.DriveSessions.Add(dbSession);
            await db.SaveChangesAsync();
            sessionData.DbSessionId = dbSession.Id;
            _cache.Set($"session:{sessionId}", sessionData, TimeSpan.FromHours(12));

            await Clients.Caller.SendAsync("SessionStarted", sessionId);
        }

        public async Task UpdatePosition(double lat, double lng, double speedKmh, double? accelMagnitude, double? dbLevel)
        {
            var sessionId = Context.ConnectionId;
            if (!_cache.TryGetValue($"session:{sessionId}", out DriveSessionData? session) || session == null)
                return;

            var position = new GeoCoordinate { Lat = lat, Lng = lng };
            session.LastPosition = position;
            session.CurrentSpeedKmh = speedKmh;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await db.AlertSettings.FirstOrDefaultAsync() ?? new AlertSettings();

            if (_cache.TryGetValue($"route:{sessionId}", out RouteGraph? route) && route != null && route.IsLoaded)
            {
                var currentSegment = FindCurrentSegment(route, position);
                if (currentSegment != null)
                {
                    session.CurrentSegment = currentSegment;

                    var mergeEvents = route.Events
                        .Where(e => e.Type == RouteEventType.OnRamp || e.Type == RouteEventType.Merge)
                        .OrderBy(e => position.DistanceTo(e.Coord))
                        .FirstOrDefault();

                    if (mergeEvents != null)
                    {
                        var mergeAlert = _merge.Evaluate(position, speedKmh, mergeEvents, accelMagnitude, session.MergedToHighway, sessionId);
                        if (mergeAlert?.HasAlert == true)
                        {
                            session.MergeAlertCount++;
                            await Clients.Caller.SendAsync("Alert", new
                            {
                                type = mergeAlert.AlertType,
                                message = mergeAlert.Message,
                                severity = mergeAlert.Severity,
                                blindSpotHold = false
                            });

                            if (position.DistanceTo(mergeEvents.Coord) < 80)
                                session.MergedToHighway = true;
                        }

                        if (session.MergedToHighway && !session.PostMergeAlertSent)
                        {
                            session.PostMergeAlertSent = true;
                            var postMerge = _merge.PostMergeAlert(sessionId);
                            await Clients.Caller.SendAsync("Alert", new
                            {
                                type = postMerge.AlertType,
                                message = postMerge.Message,
                                severity = postMerge.Severity,
                                blindSpotHold = false
                            });
                        }
                    }

                    var laneAlert = _lane.Evaluate(
                        sessionId, position, speedKmh,
                        currentSegment.SpeedLimitKmh,
                        currentSegment.LaneCount,
                        currentSegment.IsHighway,
                        accelMagnitude, route,
                        settings.PassingLaneLoiterSeconds);

                    if (laneAlert?.HasAlert == true)
                    {
                        session.LaneChangeAlertCount++;
                        await Clients.Caller.SendAsync("Alert", new
                        {
                            type = laneAlert.AlertType,
                            message = laneAlert.Message,
                            severity = laneAlert.Severity,
                            blindSpotHold = laneAlert.ShowBlindSpotHold
                        });
                    }

                    var exitEvents = route.Events
                        .Where(e => e.Type == RouteEventType.OffRamp || e.Type == RouteEventType.Exit)
                        .OrderBy(e => position.DistanceTo(e.Coord))
                        .FirstOrDefault();

                    if (exitEvents != null)
                    {
                        var exitAlert = _exit.Evaluate(
                            sessionId, position, speedKmh, exitEvents,
                            session.CurrentLane,
                            currentSegment.LaneCount);

                        if (exitAlert?.HasAlert == true)
                        {
                            session.ExitAlertCount++;

                            if (exitAlert.MissedExit)
                            {
                                await Clients.Caller.SendAsync("MissedExit", new { message = exitAlert.Message });
                            }
                            else
                            {
                                await Clients.Caller.SendAsync("Alert", new
                                {
                                    type = exitAlert.AlertType,
                                    message = exitAlert.Message,
                                    severity = exitAlert.Severity,
                                    blindSpotHold = exitAlert.ShowBlindSpotHold
                                });
                            }
                        }
                    }

                    await CheckSpeedAlert(session, currentSegment, speedKmh, settings);
                }
            }
            else
            {
                await CheckSpeedAlertNoRoute(session, speedKmh, settings);
            }

            if (dbLevel.HasValue && dbLevel.Value > settings.DistractionDbLevel)
            {
                session.DistractionAlertCount++;
                await Clients.Caller.SendAsync("Alert", new
                {
                    type = "distraction",
                    message = $"Distraction alert — noise level {dbLevel.Value:0} dB",
                    severity = "warning",
                    blindSpotHold = false
                });
            }

            if (session.LastPosition != null && session.PreviousPosition != null)
            {
                var delta = session.PreviousPosition.DistanceTo(position) / 1000.0;
                session.TotalDistanceKm += delta;
                session.SpeedReadings.Add(speedKmh);
            }
            session.PreviousPosition = position;

            _cache.Set($"session:{sessionId}", session, TimeSpan.FromHours(12));
            var currentLimitKmh = session.CurrentSegment?.SpeedLimitKmh;
            await Clients.Caller.SendAsync("PositionAck", new { lat, lng, speedKmh, speedLimitKmh = currentLimitKmh });
        }

        public Task UpdateLane(int laneIndex)
        {
            var sessionId = Context.ConnectionId;
            _lane.UpdateLane(sessionId, laneIndex);
            if (_cache.TryGetValue($"session:{sessionId}", out DriveSessionData? session) && session != null)
            {
                session.CurrentLane = laneIndex;
                _cache.Set($"session:{sessionId}", session, TimeSpan.FromHours(12));
            }
            return Task.CompletedTask;
        }

        public Task SetDrivingConditions(bool nightMode)
        {
            var sessionId = Context.ConnectionId;
            if (_cache.TryGetValue($"session:{sessionId}", out DriveSessionData? session) && session != null)
            {
                session.NightModeActive = nightMode;
                _cache.Set($"session:{sessionId}", session, TimeSpan.FromHours(12));
            }
            return Task.CompletedTask;
        }

        public async Task LoadRoute(double startLat, double startLng, double endLat, double endLng)
        {
            var sessionId = Context.ConnectionId;
            using var scope = _scopeFactory.CreateScope();
            var routeService = scope.ServiceProvider.GetRequiredService<RouteService>();
            var graph = await routeService.LoadRouteAsync(startLat, startLng, endLat, endLng);
            _cache.Set($"route:{sessionId}", graph, TimeSpan.FromHours(2));

            var payload = new
            {
                isLoaded = graph.IsLoaded,
                errorMessage = graph.ErrorMessage,
                totalDistanceMetres = graph.TotalDistanceMetres,
                segmentCount = graph.Segments.Count,
                eventCount = graph.Events.Count,
                segments = graph.Segments.Select(s => new
                {
                    s.StartCoord, s.EndCoord, s.SpeedLimitKmh, s.LaneCount, s.IsHighway, s.HasConstruction
                }),
                events = graph.Events.Select(e => new
                {
                    type = e.Type.ToString(),
                    e.Coord, e.Description, e.TotalLanes, e.ExitLaneIndex
                })
            };
            await Clients.Caller.SendAsync("RouteLoaded", payload);
        }

        public async Task EndSession()
        {
            var sessionId = Context.ConnectionId;
            if (!_cache.TryGetValue($"session:{sessionId}", out DriveSessionData? session) || session == null)
                return;

            var score = ComputeConsistencyScore(session.SpeedReadings);
            var avgSpeed = session.SpeedReadings.Count > 0 ? session.SpeedReadings.Average() : 0;

            var summary = new
            {
                totalDistanceKm = Math.Round(session.TotalDistanceKm, 2),
                averageSpeedKmh = Math.Round(avgSpeed, 1),
                speedConsistencyScore = Math.Round(score, 1),
                speedAlertCount = session.SpeedAlertCount,
                distractionAlertCount = session.DistractionAlertCount,
                backingAlertCount = session.BackingAlertCount,
                laneChangeAlertCount = session.LaneChangeAlertCount,
                mergeAlertCount = session.MergeAlertCount,
                exitAlertCount = session.ExitAlertCount,
                durationMinutes = Math.Round((DateTime.UtcNow - session.StartedAt).TotalMinutes, 1)
            };

            if (session.DbSessionId.HasValue)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var dbSession = await db.DriveSessions.FindAsync(session.DbSessionId.Value);
                if (dbSession != null)
                {
                    dbSession.EndedAt = DateTime.UtcNow;
                    dbSession.TotalDistanceKm = session.TotalDistanceKm;
                    dbSession.AverageSpeedKmh = avgSpeed;
                    dbSession.SpeedConsistencyScore = score;
                    dbSession.SpeedAlertCount = session.SpeedAlertCount;
                    dbSession.DistractionAlertCount = session.DistractionAlertCount;
                    dbSession.BackingAlertCount = session.BackingAlertCount;
                    dbSession.LaneChangeAlertCount = session.LaneChangeAlertCount;
                    dbSession.MergeAlertCount = session.MergeAlertCount;
                    dbSession.ExitAlertCount = session.ExitAlertCount;
                    await db.SaveChangesAsync();
                }
            }

            _merge.ResetSession(sessionId);
            _lane.ResetSession(sessionId);
            _exit.ResetSession(sessionId);
            _cache.Remove($"session:{sessionId}");
            _cache.Remove($"route:{sessionId}");

            await Clients.Caller.SendAsync("SessionEnded", summary);
        }

        public async Task BackingAlert()
        {
            var sessionId = Context.ConnectionId;
            if (_cache.TryGetValue($"session:{sessionId}", out DriveSessionData? session) && session != null)
            {
                session.BackingAlertCount++;
                _cache.Set($"session:{sessionId}", session, TimeSpan.FromHours(12));
            }
            await Clients.Caller.SendAsync("Alert", new
            {
                type = "backing",
                message = "Backing alert — object detected behind vehicle",
                severity = "danger",
                blindSpotHold = false
            });
        }

        private static RouteSegment? FindCurrentSegment(RouteGraph route, GeoCoordinate position)
        {
            return route.Segments
                .OrderBy(s => Math.Min(
                    position.DistanceTo(s.StartCoord),
                    position.DistanceTo(s.EndCoord)))
                .FirstOrDefault();
        }

        private async Task CheckSpeedAlert(DriveSessionData session, RouteSegment segment, double speedKmh, AlertSettings settings)
        {
            var now = DateTime.UtcNow;
            var pollMs = settings.SpeedAlertPollIntervalMinutes * 60 * 1000;
            if (segment.HasConstruction) pollMs /= 2;
            if (session.LastSpeedAlertAt.HasValue &&
                (now - session.LastSpeedAlertAt.Value).TotalMilliseconds < pollMs)
                return;

            var limit = segment.SpeedLimitKmh;
            if (segment.HasConstruction) limit -= 16;

            var yellowOver = settings.YellowDistanceThreshold;
            var redOver = settings.RedDistanceThreshold;

            if (session.NightModeActive)
            {
                var nightMargin = settings.FollowingDistanceMetres > 40 ? 5 : 8;
                yellowOver = Math.Max(0, yellowOver - nightMargin);
                redOver = Math.Max(0, redOver - nightMargin);
            }

            if (speedKmh > limit + redOver)
            {
                session.SpeedAlertCount++;
                session.LastSpeedAlertAt = now;
                await Clients.Caller.SendAsync("Alert", new
                {
                    type = "speed-red",
                    message = $"Speed alert — {speedKmh:0} km/h in {limit:0} zone",
                    severity = "danger",
                    blindSpotHold = false
                });
            }
            else if (speedKmh > limit + yellowOver)
            {
                session.SpeedAlertCount++;
                session.LastSpeedAlertAt = now;
                await Clients.Caller.SendAsync("Alert", new
                {
                    type = "speed-yellow",
                    message = $"Speed caution — {speedKmh:0} km/h in {limit:0} zone",
                    severity = "warning",
                    blindSpotHold = false
                });
            }
        }

        private async Task CheckSpeedAlertNoRoute(DriveSessionData session, double speedKmh, AlertSettings settings)
        {
            var now = DateTime.UtcNow;
            var pollMs = settings.SpeedAlertPollIntervalMinutes * 60 * 1000;
            if (session.LastSpeedAlertAt.HasValue &&
                (now - session.LastSpeedAlertAt.Value).TotalMilliseconds < pollMs)
                return;

            if (speedKmh > 120 + settings.RedDistanceThreshold)
            {
                session.SpeedAlertCount++;
                session.LastSpeedAlertAt = now;
                await Clients.Caller.SendAsync("Alert", new
                {
                    type = "speed-red",
                    message = $"High speed — {speedKmh:0} km/h",
                    severity = "danger",
                    blindSpotHold = false
                });
            }
        }

        private static double ComputeConsistencyScore(List<double> readings)
        {
            if (readings.Count < 2) return 100;
            var avg = readings.Average();
            var variance = readings.Select(r => Math.Pow(r - avg, 2)).Average();
            var stdDev = Math.Sqrt(variance);
            return Math.Max(0, 100 - stdDev);
        }
    }

    public class DriveSessionData
    {
        public string SessionId { get; set; } = string.Empty;
        public string Mode { get; set; } = "JustDrive";
        public string? DestinationAddress { get; set; }
        public DateTime StartedAt { get; set; }
        public int? DbSessionId { get; set; }
        public GeoCoordinate? LastPosition { get; set; }
        public GeoCoordinate? PreviousPosition { get; set; }
        public RouteSegment? CurrentSegment { get; set; }
        public double CurrentSpeedKmh { get; set; }
        public int CurrentLane { get; set; } = 1;
        public double TotalDistanceKm { get; set; }
        public List<double> SpeedReadings { get; set; } = new();
        public bool MergedToHighway { get; set; }
        public bool PostMergeAlertSent { get; set; }
        public DateTime? LastSpeedAlertAt { get; set; }
        public bool NightModeActive { get; set; }
        public int SpeedAlertCount { get; set; }
        public int DistractionAlertCount { get; set; }
        public int BackingAlertCount { get; set; }
        public int LaneChangeAlertCount { get; set; }
        public int MergeAlertCount { get; set; }
        public int ExitAlertCount { get; set; }
    }
}
