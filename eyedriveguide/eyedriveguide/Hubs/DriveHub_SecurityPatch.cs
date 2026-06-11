// ============================================================
// DriveHub — Security Patch Diff (juneeyedrivesafeguide)
// This file shows ONLY the security-relevant changes.
// Apply these to the full DriveHub.cs in the repo.
//
// SECURITY FIXES APPLIED:
//   AS-1  — [Authorize] attribute
//   AS-4  — Rate limiting applied via Program.cs RequireRateLimiting
//   AS-5  — HubInputValidator called on all methods
//   AS-9  — Session keys use server-generated GUID, not ConnectionId
//   DS-5  — PositionAck returns delta, not absolute coords
//   DS-6  — Cache TTL 30-min sliding; OnDisconnectedAsync cleanup
//   OW-5  — Alerts signed with HMAC via AlertIntegrityService
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using EyeDriveGuide.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EyeDriveGuide.Hubs;

// SECURITY FIX AS-1: Require authentication on the entire hub
[Authorize]
public class DriveHub : Hub
{
    private readonly IMemoryCache _cache;
    private readonly MergeAlgorithm _merge;
    private readonly LaneDisciplineAlgorithm _lane;
    private readonly ExitAlgorithm _exit;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AlertIntegrityService _alertSigner;
    private readonly ILogger<DriveHub> _logger;

    // SECURITY FIX AS-9: map ConnectionId → server-assigned session GUID
    // Stored in a separate short-lived cache to avoid guessable keys
    private static readonly string _connMapPrefix = "connmap:";

    public DriveHub(
        IMemoryCache cache,
        MergeAlgorithm merge,
        LaneDisciplineAlgorithm lane,
        ExitAlgorithm exit,
        IServiceScopeFactory scopeFactory,
        AlertIntegrityService alertSigner,
        ILogger<DriveHub> logger)
    {
        _cache = cache;
        _merge = merge;
        _lane = lane;
        _exit = exit;
        _scopeFactory = scopeFactory;
        _alertSigner = alertSigner;
        _logger = logger;
    }

    // ── Helper: get server-side session key ─────────────────
    private string GetSessionKey()
    {
        var connId = Context.ConnectionId;
        if (_cache.TryGetValue(_connMapPrefix + connId, out string? sessionId) && sessionId != null)
            return $"session:{sessionId}";
        return $"session:{connId}"; // fallback (pre-StartSession)
    }

    private string GetRouteKey()
    {
        var connId = Context.ConnectionId;
        if (_cache.TryGetValue(_connMapPrefix + connId, out string? sessionId) && sessionId != null)
            return $"route:{sessionId}";
        return $"route:{connId}";
    }

    // ── Sliding cache expiry (30 min) ────────────────────────
    private static readonly MemoryCacheEntryOptions _sessionCacheOpts =
        new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(30));

    private static readonly MemoryCacheEntryOptions _routeCacheOpts =
        new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(30));

    // ── Send a signed alert to the caller ───────────────────
    private async Task SendSignedAlert(object alertPayload)
    {
        // SECURITY FIX OW-5: sign alert so client can verify server origin
        var signed = _alertSigner.Sign(alertPayload);
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
            _logger.LogWarning("StartSession rejected from {ConnId}: {Error}",
                Context.ConnectionId, error);
            await Clients.Caller.SendAsync("Error", new { message = error, code = "INVALID_INPUT" });
            return;
        }

        // SECURITY FIX AS-9: assign a server-generated GUID as the real session key
        var sessionGuid = Guid.NewGuid().ToString("N");
        _cache.Set(_connMapPrefix + Context.ConnectionId, sessionGuid,
            TimeSpan.FromHours(2));

        var sessionData = new DriveSessionData
        {
            SessionId = sessionGuid,   // Use GUID, not ConnectionId
            Mode = mode,
            DestinationAddress = HubInputValidator.SanitiseForDisplay(destinationAddress),
            StartedAt = DateTime.UtcNow
        };

        _cache.Set($"session:{sessionGuid}", sessionData, _sessionCacheOpts);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbSession = new DriveSession
        {
            StartedAt = DateTime.UtcNow,
            Mode = mode,
            DestinationAddress = HubInputValidator.SanitiseForDisplay(destinationAddress),
            // SECURITY FIX OW-1: store user identity for data scoping
            UserId = Context.UserIdentifier
        };
        db.DriveSessions.Add(dbSession);
        await db.SaveChangesAsync();

        sessionData.DbSessionId = dbSession.Id;
        _cache.Set($"session:{sessionGuid}", sessionData, _sessionCacheOpts);

        await Clients.Caller.SendAsync("SessionStarted", sessionGuid);
    }

    // ═══════════════════════════════════════════════════════
    // UpdatePosition — security-critical hot path
    // ═══════════════════════════════════════════════════════
    public async Task UpdatePosition(
        double lat, double lng, double speedKmh,
        double? accelMagnitude, double? dbLevel)
    {
        // SECURITY FIX AS-5: validate all inputs before ANY processing
        var validation = HubInputValidator.ValidatePosition(lat, lng, speedKmh, accelMagnitude, dbLevel);
        if (!validation.IsValid)
        {
            _logger.LogWarning("UpdatePosition rejected from {ConnId}: {Error}",
                Context.ConnectionId, validation.Error);
            return; // Silent reject to avoid feedback loops
        }

        // Use validated/clamped values
        lat = validation.Lat;
        lng = validation.Lng;
        speedKmh = validation.SpeedKmh;
        dbLevel = validation.DbLevel;

        var sessionKey = GetSessionKey();
        if (!_cache.TryGetValue(sessionKey, out DriveSessionData? session) || session == null)
            return;

        // ... (rest of UpdatePosition logic unchanged, but uses SendSignedAlert)
        // SECURITY FIX DS-5: store start position for delta calculation
        session.LastPosition = new GeoCoordinate { Lat = lat, Lng = lng };
        session.CurrentSpeedKmh = speedKmh;

        // ... (full algorithm logic same as original) ...

        _cache.Set(sessionKey, session, _sessionCacheOpts);

        // SECURITY FIX DS-5: return delta from session start, not raw coords
        var deltaLat = session.StartPosition != null ? lat - session.StartPosition.Lat : 0;
        var deltaLng = session.StartPosition != null ? lng - session.StartPosition.Lng : 0;
        var currentLimitKmh = session.CurrentSegment?.SpeedLimitKmh;

        await Clients.Caller.SendAsync("PositionAck", new
        {
            deltaLat,          // relative, not absolute
            deltaLng,          // relative, not absolute
            speedKmh,          // needed for UI display
            speedLimitKmh = currentLimitKmh
        });
    }

    // ═══════════════════════════════════════════════════════
    // LoadRoute
    // ═══════════════════════════════════════════════════════
    public async Task LoadRoute(
        double startLat, double startLng,
        double endLat, double endLng)
    {
        // SECURITY FIX AS-5
        var (valid, error) = HubInputValidator.ValidateLoadRoute(
            startLat, startLng, endLat, endLng);
        if (!valid)
        {
            await Clients.Caller.SendAsync("Error", new { message = error, code = "INVALID_INPUT" });
            return;
        }

        // ... (original route loading logic unchanged) ...
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
            _cache.Set(sessionKey, session, _sessionCacheOpts);
        }
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════
    // OnDisconnectedAsync — SECURITY FIX DS-6
    // Eagerly evict cache on disconnect; don't wait 30 min
    // ═══════════════════════════════════════════════════════
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var connId = Context.ConnectionId;

        if (_cache.TryGetValue(_connMapPrefix + connId, out string? sessionId) && sessionId != null)
        {
            _cache.Remove($"session:{sessionId}");
            _cache.Remove($"route:{sessionId}");
            _merge.ResetSession(connId);
            _lane.ResetSession(connId);
            _exit.ResetSession(connId);
        }

        _cache.Remove(_connMapPrefix + connId);

        _logger.LogInformation("Drive hub disconnected: {ConnId}", connId);
        return base.OnDisconnectedAsync(exception);
    }
}
