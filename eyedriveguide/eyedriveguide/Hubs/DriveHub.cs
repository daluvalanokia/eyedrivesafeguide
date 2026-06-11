// ============================================================
// DriveHub.cs — Security-Updated (merged, single definition)
// REPLACES both DriveHub.cs and DriveHub_SecurityPatch.cs
// SECURITY FIXES:
//   AS-1 — [Authorize] on entire hub
//   AS-4 — Rate limiting via Program.cs RequireRateLimiting
//   AS-5 — HubInputValidator on all methods
//   AS-9 — Session keys use server GUID, not ConnectionId
//   DS-5 — PositionAck returns delta coords, not absolute
//   DS-6 — 30-min sliding cache; OnDisconnectedAsync cleanup
//   OW-1 — UserId stored on DriveSession
//   OW-5 — Alerts signed with HMAC via AlertIntegrityService
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using EyeDriveGuide.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EyeDriveGuide.Hubs;

// SECURITY FIX AS-1: require authentication on the entire hub
[Authorize]
public class DriveHub : Hub
{
    private readonly IMemoryCache            _cache;
    private readonly MergeAlgorithm          _merge;
    private readonly LaneDisciplineAlgorithm _lane;
    private readonly ExitAlgorithm           _exit;
    private readonly IServiceScopeFactory    _scopeFactory;
    private readonly AlertIntegrityService   _alertSigner;
    private readonly ILogger<DriveHub>       _logger;

    // SECURITY FIX AS-9: map ConnectionId → server-assigned session GUID
    private const string ConnMapPrefix = "connmap:";

    // SECURITY FIX DS-6: sliding 30-min expiry instead of 12-hour fixed
    private static readonly MemoryCacheEntryOptions SessionCacheOpts =
        new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(30));

    private static readonly MemoryCacheEntryOptions RouteCacheOpts =
        new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(30));

    public DriveHub(
        IMemoryCache cache,
        MergeAlgorithm merge,
        LaneDisciplineAlgorithm lane,
        ExitAlgorithm exit,
        IServiceScopeFactory scopeFactory,
        AlertIntegrityService alertSigner,
        ILogger<DriveHub> logger)
    {
        _cache        = cache;
        _merge        = merge;
        _lane         = lane;
        _exit         = exit;
        _scopeFactory = scopeFactory;
        _alertSigner  = alertSigner;
        _logger       = logger;
    }

    // ── Session key helpers ──────────────────────────────────
    private string GetSessionKey()
    {
        var connId = Context.ConnectionId;
        return _cache.TryGetValue(ConnMapPrefix + connId, out string? guid) && guid != null
            ? $"session:{guid}"
            : $"session:{connId}";
    }

    private string GetRouteKey()
    {
        var connId = Context.ConnectionId;
        return _cache.TryGetValue(ConnMapPrefix + connId, out string? guid) && guid != null
            ? $"route:{guid}"
            : $"route:{connId}";
    }

    // ── SECURITY FIX OW-5: send HMAC-signed alert ───────────
    private async Task SendSignedAlert(object payload)
    {
        var signed = _alertSigner.Sign(payload);
        await Clients.Caller.SendAsync("Alert", signed);
    }

    // ═══════════════════════════════════════════════════════
    // StartSession
    // ═══════════════════════════════════════════════════════
    public async Task StartSession(string mode, string? destinationAddress)
    {
        // SECURITY FIX AS-5: validate inputs
        var (valid, error) = HubInputValidator.ValidateStartSession(mode, destinationAddress);
        if (!valid)
        {
            _logger.LogWarning("StartSession rejected from {ConnId}: {Error}", Context.ConnectionId, error);
            await Clients.Caller.SendAsync("Error", new { message = error, code = "INVALID_INPUT" });
            return;
        }

        // SECURITY FIX AS-9: use server-generated GUID as session key
        var sessionGuid = Guid.NewGuid().ToString("N");
        _cache.Set(ConnMapPrefix + Context.ConnectionId, sessionGuid, TimeSpan.FromHours(2));

        var sanitisedDest = HubInputValidator.SanitiseForDisplay(destinationAddress);
        var sessionData = new DriveSessionData
        {
            SessionId          = sessionGuid,
            Mode               = mode,
            DestinationAddress = sanitisedDest,
            StartedAt          = DateTime.UtcNow
        };
        _cache.Set($"session:{sessionGuid}", sessionData, SessionCacheOpts);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbSession = new DriveSession
        {
            StartedAt          = DateTime.UtcNow,
            Mode               = mode,
            DestinationAddress = sanitisedDest,
            UserId             = Context.UserIdentifier  // SECURITY FIX OW-1
        };
        db.DriveSessions.Add(dbSession);
        await db.SaveChangesAsync();

        sessionData.DbSessionId = dbSession.Id;
        _cache.Set($"session:{sessionGuid}", sessionData, SessionCacheOpts);

        await Clients.Caller.SendAsync("SessionStarted", sessionGuid);
    }

    // ═══════════════════════════════════════════════════════
    // UpdatePosition
    // ═══════════════════════════════════════════════════════
    public async Task UpdatePosition(
        double lat, double lng, double speedKmh,
        double? accelMagnitude, double? dbLevel,
        double? frontDistanceM = null)   // PR #2: following distance
    {
        // SECURITY FIX AS-5: validate all sensor inputs
        var validation = HubInputValidator.ValidatePosition(lat, lng, speedKmh, accelMagnitude, dbLevel);
        if (!validation.IsValid)
        {
            _logger.LogWarning("UpdatePosition rejected from {ConnId}: {Error}",
                Context.ConnectionId, validation.Error);
            return;
        }

        lat      = validation.Lat;
        lng      = validation.Lng;
        speedKmh = validation.SpeedKmh;
        dbLevel  = validation.DbLevel;

        var sessionKey = GetSessionKey();
        if (!_cache.TryGetValue(sessionKey, out DriveSessionData? session) || session == null)
            return;

        var position = new GeoCoordinate { Lat = lat, Lng = lng };

        // Record start position for delta calculation (SECURITY FIX DS-5)
        session.StartPosition ??= position;
        session.LastPosition    = position;
        session.CurrentSpeedKmh = speedKmh;

        using var scope = _scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.AlertSettings.FirstOrDefaultAsync() ?? new AlertSettings();

        if (_cache.TryGetValue(GetRouteKey(), out RouteGraph? route) && route?.IsLoaded == true)
        {
            var currentSegment = FindCurrentSegment(route, position);
            if (currentSegment != null)
            {
                session.CurrentSegment = currentSegment;

                // Merge / on-ramp alert
                var mergeEvent = route.Events
                    .Where(e => e.Type == RouteEventType.OnRamp || e.Type == RouteEventType.Merge)
                    .OrderBy(e => position.DistanceTo(e.Coord))
                    .FirstOrDefault();

                if (mergeEvent != null)
                {
                    var mergeAlert = _merge.Evaluate(position, speedKmh, mergeEvent,
                        accelMagnitude, session.MergedToHighway, session.SessionId);
                    if (mergeAlert?.HasAlert == true)
                    {
                        session.MergeAlertCount++;
                        await SendSignedAlert(new {
                            type = mergeAlert.AlertType, message = mergeAlert.Message,
                            severity = mergeAlert.Severity, blindSpotHold = false });
                        if (position.DistanceTo(mergeEvent.Coord) < 80)
                            session.MergedToHighway = true;
                    }
                    if (session.MergedToHighway && !session.PostMergeAlertSent)
                    {
                        session.PostMergeAlertSent = true;
                        var pm = _merge.PostMergeAlert(session.SessionId);
                        await SendSignedAlert(new {
                            type = pm.AlertType, message = pm.Message,
                            severity = pm.Severity, blindSpotHold = false });
                    }
                }

                // Lane discipline alert
                var laneAlert = _lane.Evaluate(
                    session.SessionId, position, speedKmh,
                    currentSegment.SpeedLimitKmh, currentSegment.LaneCount,
                    currentSegment.IsHighway, accelMagnitude,
                    route, settings.PassingLaneLoiterSeconds);
                if (laneAlert?.HasAlert == true)
                {
                    session.LaneChangeAlertCount++;
                    await SendSignedAlert(new {
                        type = laneAlert.AlertType, message = laneAlert.Message,
                        severity = laneAlert.Severity, blindSpotHold = laneAlert.ShowBlindSpotHold });
                }

                // Exit alert
                var exitEvent = route.Events
                    .Where(e => e.Type == RouteEventType.OffRamp || e.Type == RouteEventType.Exit)
                    .OrderBy(e => position.DistanceTo(e.Coord))
                    .FirstOrDefault();
                if (exitEvent != null)
                {
                    var exitAlert = _exit.Evaluate(session.SessionId, position, speedKmh,
                        exitEvent, session.CurrentLane, currentSegment.LaneCount);
                    if (exitAlert?.HasAlert == true)
                    {
                        session.ExitAlertCount++;
                        if (exitAlert.MissedExit)
                            await Clients.Caller.SendAsync("MissedExit", new { message = exitAlert.Message });
                        else
                            await SendSignedAlert(new {
                                type = exitAlert.AlertType, message = exitAlert.Message,
                                severity = exitAlert.Severity, blindSpotHold = exitAlert.ShowBlindSpotHold });
                    }
                }

                await CheckSpeedAlert(session, currentSegment, speedKmh, settings);
            }
        }
        else
        {
            await CheckSpeedAlertNoRoute(session, speedKmh, settings);
        }

        // Distraction alert
        if (dbLevel.HasValue && dbLevel.Value > settings.DistractionDbLevel)
        {
            session.DistractionAlertCount++;
            await SendSignedAlert(new {
                type = "distraction",
                message = $"Distraction alert — noise level {dbLevel.Value:0} dB",
                severity = "warning", blindSpotHold = false });
        }

        // Odometer
        if (session.LastPosition != null && session.PreviousPosition != null)
        {
            var delta = session.PreviousPosition.DistanceTo(position) / 1000.0;
            session.TotalDistanceKm += delta;
            session.SpeedReadings.Add(speedKmh);
        }
        session.PreviousPosition = position;

        _cache.Set(sessionKey, session, SessionCacheOpts);

        // SECURITY FIX DS-5: return relative delta, not absolute GPS coords
        var deltaLat = position.Lat - (session.StartPosition?.Lat ?? position.Lat);
        var deltaLng = position.Lng - (session.StartPosition?.Lng ?? position.Lng);
        await Clients.Caller.SendAsync("PositionAck", new {
            deltaLat,
            deltaLng,
            speedKmh,
            speedLimitKmh = session.CurrentSegment?.SpeedLimitKmh
        });
    }

    // ═══════════════════════════════════════════════════════
    // UpdateLane
    // ═══════════════════════════════════════════════════════
    public Task UpdateLane(int laneIndex)
    {
        // SECURITY FIX AS-5
        var (valid, error) = HubInputValidator.ValidateUpdateLane(laneIndex);
        if (!valid)
        {
            _logger.LogWarning("UpdateLane rejected: {Error}", error);
            return Task.CompletedTask;
        }

        var sessionKey = GetSessionKey();
        _lane.UpdateLane(Context.ConnectionId, laneIndex);
        if (_cache.TryGetValue(sessionKey, out DriveSessionData? session) && session != null)
        {
            session.CurrentLane = laneIndex;
            _cache.Set(sessionKey, session, SessionCacheOpts);
        }
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════
    // SetDrivingConditions
    // ═══════════════════════════════════════════════════════
    public Task SetDrivingConditions(bool nightMode)
    {
        var sessionKey = GetSessionKey();
        if (_cache.TryGetValue(sessionKey, out DriveSessionData? session) && session != null)
        {
            session.NightModeActive = nightMode;
            _cache.Set(sessionKey, session, SessionCacheOpts);
        }
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════
    // LoadRoute
    // ═══════════════════════════════════════════════════════
    public async Task LoadRoute(
        double startLat, double startLng,
        double endLat,   double endLng)
    {
        // SECURITY FIX AS-5: validate coordinates
        var (valid, error) = HubInputValidator.ValidateLoadRoute(startLat, startLng, endLat, endLng);
        if (!valid)
        {
            await Clients.Caller.SendAsync("Error", new { message = error, code = "INVALID_INPUT" });
            return;
        }

        var routeKey = GetRouteKey();
        using var scope = _scopeFactory.CreateScope();
        var routeService = scope.ServiceProvider.GetRequiredService<RouteService>();
        var graph = await routeService.LoadRouteAsync(startLat, startLng, endLat, endLng);
        _cache.Set(routeKey, graph, RouteCacheOpts);

        var payload = new {
            isLoaded             = graph.IsLoaded,
            errorMessage         = graph.ErrorMessage,
            totalDistanceMetres  = graph.TotalDistanceMetres,
            segmentCount         = graph.Segments.Count,
            eventCount           = graph.Events.Count,
            segments = graph.Segments.Select(s => new {
                s.StartCoord, s.EndCoord, s.SpeedLimitKmh,
                s.LaneCount, s.IsHighway, s.HasConstruction,
                s.DistanceMetres }),
            events = graph.Events.Select(e => new {
                type = e.Type.ToString(), e.Coord,
                e.Description, e.TotalLanes, e.ExitLaneIndex })
        };
        await Clients.Caller.SendAsync("RouteLoaded", payload);
    }

    // ═══════════════════════════════════════════════════════
    // BackingAlert
    // ═══════════════════════════════════════════════════════
    public async Task BackingAlert()
    {
        var sessionKey = GetSessionKey();
        if (_cache.TryGetValue(sessionKey, out DriveSessionData? session) && session != null)
        {
            session.BackingAlertCount++;
            _cache.Set(sessionKey, session, SessionCacheOpts);
        }
        await SendSignedAlert(new {
            type = "backing", message = "Vehicle behind — check rear",
            severity = "warning", blindSpotHold = false });
    }

    // ═══════════════════════════════════════════════════════
    // EndSession
    // ═══════════════════════════════════════════════════════
    public async Task EndSession()
    {
        var sessionKey = GetSessionKey();
        if (!_cache.TryGetValue(sessionKey, out DriveSessionData? session) || session == null)
            return;

        var score    = ComputeConsistencyScore(session.SpeedReadings);
        var avgSpeed = session.SpeedReadings.Count > 0 ? session.SpeedReadings.Average() : 0;

        var summary = new {
            totalDistanceKm        = Math.Round(session.TotalDistanceKm, 2),
            averageSpeedKmh        = Math.Round(avgSpeed, 1),
            speedConsistencyScore  = Math.Round(score, 1),
            speedAlertCount        = session.SpeedAlertCount,
            distractionAlertCount  = session.DistractionAlertCount,
            backingAlertCount      = session.BackingAlertCount,
            laneChangeAlertCount   = session.LaneChangeAlertCount,
            mergeAlertCount        = session.MergeAlertCount,
            exitAlertCount         = session.ExitAlertCount,
            durationMinutes        = Math.Round((DateTime.UtcNow - session.StartedAt).TotalMinutes, 1)
        };

        if (session.DbSessionId.HasValue)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbSession = await db.DriveSessions.FindAsync(session.DbSessionId.Value);
            if (dbSession != null)
            {
                dbSession.EndedAt               = DateTime.UtcNow;
                dbSession.TotalDistanceKm       = session.TotalDistanceKm;
                dbSession.AverageSpeedKmh       = avgSpeed;
                dbSession.SpeedConsistencyScore = score;
                dbSession.SpeedAlertCount       = session.SpeedAlertCount;
                dbSession.DistractionAlertCount = session.DistractionAlertCount;
                dbSession.BackingAlertCount     = session.BackingAlertCount;
                dbSession.LaneChangeAlertCount  = session.LaneChangeAlertCount;
                dbSession.MergeAlertCount       = session.MergeAlertCount;
                dbSession.ExitAlertCount        = session.ExitAlertCount;
                await db.SaveChangesAsync();
            }
        }

        _merge.ResetSession(Context.ConnectionId);
        _lane.ResetSession(Context.ConnectionId);
        _exit.ResetSession(Context.ConnectionId);
        _cache.Remove(sessionKey);
        _cache.Remove(GetRouteKey());
        _cache.Remove(ConnMapPrefix + Context.ConnectionId);

        await Clients.Caller.SendAsync("SessionEnded", summary);
    }

    // ═══════════════════════════════════════════════════════
    // OnDisconnectedAsync — SECURITY FIX DS-6
    // Eagerly evict cache on disconnect
    // ═══════════════════════════════════════════════════════
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var connId = Context.ConnectionId;
        if (_cache.TryGetValue(ConnMapPrefix + connId, out string? sessionId) && sessionId != null)
        {
            _cache.Remove($"session:{sessionId}");
            _cache.Remove($"route:{sessionId}");
            _merge.ResetSession(connId);
            _lane.ResetSession(connId);
            _exit.ResetSession(connId);
        }
        _cache.Remove(ConnMapPrefix + connId);
        _logger.LogInformation("DriveHub disconnected: {ConnId}", connId);
        return base.OnDisconnectedAsync(exception);
    }

    // ═══════════════════════════════════════════════════════
    // Private helpers (unchanged from original)
    // ═══════════════════════════════════════════════════════
    private static RouteSegment? FindCurrentSegment(RouteGraph route, GeoCoordinate position)
    {
        return route.Segments
            .OrderBy(s => PointToSegmentDistance(position, s.StartCoord, s.EndCoord))
            .FirstOrDefault();
    }

    private static double PointToSegmentDistance(GeoCoordinate p, GeoCoordinate a, GeoCoordinate b)
    {
        var abLat = b.Lat - a.Lat; var abLng = b.Lng - a.Lng;
        var apLat = p.Lat - a.Lat; var apLng = p.Lng - a.Lng;
        var ab2 = abLat * abLat + abLng * abLng;
        if (ab2 == 0) return p.DistanceTo(a);
        var t = Math.Max(0, Math.Min(1, (apLat * abLat + apLng * abLng) / ab2));
        return p.DistanceTo(new GeoCoordinate { Lat = a.Lat + t * abLat, Lng = a.Lng + t * abLng });
    }

    private async Task CheckSpeedAlert(DriveSessionData session, RouteSegment segment,
        double speedKmh, AlertSettings settings)
    {
        var limit = segment.SpeedLimitKmh;
        var night = session.NightModeActive && settings.WeatherNightModeAutoAdjust ? -8 : 0;
        var yellow = limit + settings.YellowDistanceThreshold + night;
        var red    = limit + settings.RedDistanceThreshold    + night;

        var now = DateTime.UtcNow;
        var cooldownMins = settings.SpeedAlertPollIntervalMinutes;
        if (session.LastSpeedAlertAt.HasValue &&
            (now - session.LastSpeedAlertAt.Value).TotalMinutes < cooldownMins) return;

        if (speedKmh >= red)
        {
            session.SpeedAlertCount++;
            session.LastSpeedAlertAt = now;
            await SendSignedAlert(new {
                type = "speed-red",
                message = $"Speed {speedKmh:0} km/h — {speedKmh - limit:0} over limit",
                severity = "danger", blindSpotHold = false });
        }
        else if (speedKmh >= yellow)
        {
            session.SpeedAlertCount++;
            session.LastSpeedAlertAt = now;
            await SendSignedAlert(new {
                type = "speed-yellow",
                message = $"Approaching speed limit ({limit} km/h)",
                severity = "warning", blindSpotHold = false });
        }
    }

    private async Task CheckSpeedAlertNoRoute(DriveSessionData session,
        double speedKmh, AlertSettings settings)
    {
        const double defaultLimit = 80;
        var now = DateTime.UtcNow;
        if (session.LastSpeedAlertAt.HasValue &&
            (now - session.LastSpeedAlertAt.Value).TotalMinutes < settings.SpeedAlertPollIntervalMinutes) return;

        if (speedKmh > defaultLimit + settings.RedDistanceThreshold)
        {
            session.SpeedAlertCount++;
            session.LastSpeedAlertAt = now;
            await SendSignedAlert(new {
                type = "speed-red",
                message = $"Speed {speedKmh:0} km/h — above {defaultLimit} km/h default limit",
                severity = "danger", blindSpotHold = false });
        }
    }

    private static double ComputeConsistencyScore(List<double> readings)
    {
        if (readings.Count < 2) return 100;
        var avg = readings.Average();
        if (avg == 0) return 100;
        var stdDev = Math.Sqrt(readings.Sum(r => Math.Pow(r - avg, 2)) / readings.Count);
        return Math.Max(0, Math.Round(100 - (stdDev / avg * 100), 1));
    }
}

// ============================================================
// DriveSessionData — in-memory session state
// Restored: was accidentally dropped during security merge.
// Extended with StartPosition (DS-5) and simulation fields.
// ============================================================
public class DriveSessionData
{
    // ── Identity ─────────────────────────────────────────────
    public string  SessionId          { get; set; } = string.Empty;
    public string  Mode               { get; set; } = "JustDrive";
    public string? DestinationAddress { get; set; }
    public int?    DbSessionId        { get; set; }
    public DateTime StartedAt         { get; set; }

    // ── Position ─────────────────────────────────────────────
    public GeoCoordinate? StartPosition    { get; set; }  // SECURITY FIX DS-5: for delta PositionAck
    public GeoCoordinate? LastPosition     { get; set; }
    public GeoCoordinate? PreviousPosition { get; set; }

    // ── Route / segment ──────────────────────────────────────
    public RouteSegment? CurrentSegment { get; set; }

    // ── Drive state ──────────────────────────────────────────
    public double CurrentSpeedKmh  { get; set; }
    public int    CurrentLane      { get; set; } = 1;
    public double TotalDistanceKm  { get; set; }
    public List<double> SpeedReadings { get; set; } = new();
    public bool NightModeActive    { get; set; }
    public bool AdverseWeather     { get; set; }

    // ── Merge / exit flags ───────────────────────────────────
    public bool MergedToHighway    { get; set; }
    public bool PostMergeAlertSent { get; set; }

    // ── Alert cooldown ───────────────────────────────────────
    public DateTime? LastSpeedAlertAt { get; set; }

    // ── Alert counters ───────────────────────────────────────
    public int SpeedAlertCount       { get; set; }
    public int DistractionAlertCount { get; set; }
    public int BackingAlertCount     { get; set; }
    public int LaneChangeAlertCount  { get; set; }
    public int MergeAlertCount       { get; set; }
    public int ExitAlertCount        { get; set; }

    // ── Following distance (PR #2) ───────────────────────────
    public List<DistanceSample> FollowingDistanceSamples  { get; set; } = new();
    public Dictionary<SpeedBand, double>? PersonalBaselines { get; set; }
    public int FollowingDistanceAlertCount { get; set; }
}

