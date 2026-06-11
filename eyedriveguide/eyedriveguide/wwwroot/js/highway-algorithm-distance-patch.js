// ============================================================
// highway-algorithm-distance-patch.js
// PATCH: Wire DistanceMonitor into the existing highway-algorithm.js.
//
// Apply this diff manually to highway-algorithm.js, or include this
// file after highway-algorithm.js and call applyDistancePatch().
//
// Changes:
//   1. Start DistanceMonitor on drive start
//   2. Pass frontDistanceM into every UpdatePosition hub call
//   3. Handle new 'FollowingAlert' SignalR event
//   4. Show following distance in the HUD
//   5. Stop DistanceMonitor on drive stop
// ============================================================

// ── Module-level state additions ────────────────────────────
let frontDistanceM = null;      // Current distance to vehicle ahead (metres)
let distanceSource = null;      // 'camera' | 'serial' | null

// ── Patch: Wire into buildHub() ─────────────────────────────
// Add this handler inside buildHub(), after existing hubConnection.on() calls:

function _registerFollowingDistanceHubHandlers() {
  // Receives following-distance alerts from FollowingDistanceAlgorithm
  hubConnection.on('FollowingAlert', data => {
    AlertSystem.show(data);
    _updateDistanceHud(data.currentDistanceM, data.recommendedDistanceM, data.level);
  });

  // Server echoes back suppressed state (so UI can show "Jam detected")
  hubConnection.on('FollowingDistanceSuppressed', data => {
    _updateDistanceHud(data.currentDistanceM, null, 'suppressed');
  });
}

// ── Patch: Wire into startDrive() ───────────────────────────
// Call this at the end of startDrive(), before the GPS loop starts:

async function _startDistanceMonitor() {
  await DistanceMonitor.start(distanceM => {
    frontDistanceM = distanceM;
  });
  distanceSource = DistanceMonitor.getSource();

  const sourceEl = document.getElementById('distanceSource');
  if (sourceEl) {
    sourceEl.textContent = distanceSource
      ? `Distance sensor: ${distanceSource}`
      : 'Distance sensor: unavailable';
  }
}

// ── Patch: UpdatePosition call ───────────────────────────────
// Replace the existing hubConnection.invoke('UpdatePosition', ...) call with:
//
// ORIGINAL:
//   hubConnection.invoke('UpdatePosition', lat, lng, kmh, accelMag, currentDbLevel)
//
// PATCHED (add frontDistanceM as 6th argument):
//   hubConnection.invoke('UpdatePosition', lat, lng, kmh, accelMag, currentDbLevel, frontDistanceM)
//
// The DriveHub.UpdatePosition signature also needs updating — see DriveHub patch below.

function _patchedUpdatePosition(lat, lng, kmh) {
  if (hubConnection?.state !== signalR.HubConnectionState.Connected) return;
  hubConnection.invoke(
    'UpdatePosition',
    lat,
    lng,
    kmh,
    accelMag ?? 0,
    currentDbLevel ?? 0,
    frontDistanceM   // NEW: null if sensor unavailable, server ignores null
  ).catch(err => console.warn('UpdatePosition error:', err));
}

// ── Patch: stopDrive() addition ─────────────────────────────
function _stopDistanceMonitor() {
  DistanceMonitor.stop();
  frontDistanceM = null;
  _updateDistanceHud(null, null, null);
}

// ── HUD: Distance display ────────────────────────────────────
function _updateDistanceHud(currentM, recommendedM, level) {
  const el = document.getElementById('followingDistanceHud');
  if (!el) return;

  if (currentM == null) {
    el.style.display = 'none';
    return;
  }

  el.style.display = 'block';

  // Colour coding
  const levelColors = {
    'None':          '#28a745',   // green
    'Advisory':      '#17a2b8',   // blue
    'Warning':       '#ffc107',   // yellow
    'Critical':      '#dc3545',   // red
    'PersonalDrift': '#6f42c1',   // purple
    'suppressed':    '#6c757d',   // grey (jam)
  };
  const color = levelColors[level] || '#6c757d';

  const distText = currentM != null ? `${currentM.toFixed(0)} m` : '—';
  const recText  = recommendedM != null ? ` / ${recommendedM.toFixed(0)} m ideal` : '';

  // Safe DOM construction (no innerHTML with data)
  el.style.borderColor = color;
  el.querySelector('.dist-current').textContent = distText;
  el.querySelector('.dist-recommended').textContent = recText;
  el.querySelector('.dist-icon').textContent =
    level === 'Critical'  ? '⚠️' :
    level === 'Warning'   ? '🟡' :
    level === 'suppressed'? '🚦' : '📏';
}

// ── HTML snippet for distance HUD ────────────────────────────
// Add this to Views/Navigation/Index.cshtml, inside the HUD panel:
/*
<div id="followingDistanceHud" class="edg-dist-hud" style="display:none">
  <span class="dist-icon">📏</span>
  <span class="dist-current">—</span>
  <span class="dist-recommended"></span>
</div>
<div id="distanceSource" class="edg-dist-source text-muted small"></div>
*/

// ── CSS additions (add to site.css) ─────────────────────────
/*
.edg-dist-hud {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 2px solid #6c757d;
  border-radius: 8px;
  padding: 4px 10px;
  font-size: 1.1rem;
  font-weight: 600;
  transition: border-color 0.3s;
}
.edg-dist-source { font-size: 0.75rem; margin-top: 2px; }
*/

// ── DriveHub.cs patch for UpdatePosition signature ───────────
// Change:
//   public async Task UpdatePosition(double lat, double lng, double speedKmh,
//       double? accelMagnitude, double? dbLevel)
// To:
//   public async Task UpdatePosition(double lat, double lng, double speedKmh,
//       double? accelMagnitude, double? dbLevel, double? frontDistanceM)
//
// Then inside UpdatePosition, after the distraction alert block, add:
/*
  // Following distance evaluation
  if (frontDistanceM.HasValue)
  {
      var userId = Context.UserIdentifier;
      var bandBaseline = session.PersonalBaselines != null &&
          session.PersonalBaselines.TryGetValue(
              FollowingDistanceAlgorithm.ClassifySpeedBand(speedKmh), out var bl)
          ? bl : (double?)null;

      var followAlert = _followingDistance.Evaluate(
          sessionId, speedKmh, frontDistanceM, bandBaseline, settings,
          session.NightModeActive, session.AdverseWeather);

      session.FollowingDistanceSamples.Add(
          new DistanceSample(DateTime.UtcNow, speedKmh, frontDistanceM.Value,
              FollowingDistanceAlgorithm.ClassifySpeedBand(speedKmh),
              followAlert.Suppressed));

      if (followAlert.HasAlert)
      {
          session.FollowingDistanceAlertCount++;
          await Clients.Caller.SendAsync("FollowingAlert", new {
              type     = followAlert.AlertType,
              message  = followAlert.Message,
              severity = followAlert.Severity,
              level    = followAlert.Level.ToString(),
              currentDistanceM     = followAlert.CurrentDistanceM,
              recommendedDistanceM = followAlert.RecommendedDistanceM,
              band     = followAlert.Band.ToString(),
              blindSpotHold = false
          });
      }
      else if (followAlert.Suppressed)
      {
          await Clients.Caller.SendAsync("FollowingDistanceSuppressed", new {
              currentDistanceM = followAlert.CurrentDistanceM,
              reason = followAlert.SuppressReason
          });
      }
  }
*/

// ── Apply patch function (call once on page load) ────────────
function applyDistancePatch() {
  // Register additional hub handlers after buildHub() is called
  if (typeof hubConnection !== 'undefined' && hubConnection) {
    _registerFollowingDistanceHubHandlers();
  }
  console.log('[DistancePatch] Following distance patch applied');
}
