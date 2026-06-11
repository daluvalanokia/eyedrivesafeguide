// ============================================================
// simulation.js — Drive Simulation Engine
// EyeDriveSafeGuide | juneeyedrivesafeguide
//
// Strategy: Hybrid client-driven simulation.
// Walks routeData.segments geometry, synthesises GPS positions,
// and feeds them into the SAME hubConnection.invoke('UpdatePosition')
// that real GPS uses. All existing server algorithms fire identically.
//
// Global flag (default off):
//   window.EDG_SIMULATION = false
//
// Enable via: SimulationEngine.enable() or ?sim=1 query string
// ============================================================

'use strict';

const SimulationEngine = (() => {

  // ── Configuration defaults ────────────────────────────────
  const TICK_MS           = 500;   // Position update interval
  const SPEED_STEP_KMH    = 5;     // +/- button increment
  const MIN_SPEED_KMH     = 0;
  const MAX_SPEED_KMH     = 120;
  const DEFAULT_SPEED_KMH = 50;
  const CLOSE_GAP_M       = 6;     // "Force close gap" test distance
  const DIST_NOISE_PCT    = 0.15;  // ±15% distance noise for realism

  // ── State ─────────────────────────────────────────────────
  let _enabled   = false;
  let _running   = false;
  let _paused    = false;
  let _tickTimer = null;

  let _waypoints = [];          // Flat array of {lat, lng, cumulativeM}
  let _totalRouteM = 0;
  let _travelledM  = 0;

  let _simSpeedKmh  = DEFAULT_SPEED_KMH;
  let _simLane      = 1;          // Current lane index (0 = leftmost)
  let _simLaneCount = 2;
  let _simBearing   = 0;          // Car heading in degrees

  let _prevLat = null;
  let _prevLng = null;

  let _carMarker = null;          // Leaflet marker for car icon
  let _carIcon   = null;

  let _onTick  = null;            // Callback: (lat, lng, kmh, bearing, travelledM, totalM)
  let _onDone  = null;            // Callback: () when destination reached
  let _onAlert = null;            // Callback: (msg) for simulation-specific status

  // Distance override for following-distance testing
  let _forcedDistanceM = null;    // null = auto-compute

  // ═══════════════════════════════════════════════════════════
  // Public API
  // ═══════════════════════════════════════════════════════════

  /** Enable simulation mode. Does NOT start the drive — call startDrive() for that. */
  function enable() {
    _enabled = true;
    window.EDG_SIMULATION = true;
    _showPanel(true);
    console.log('[Sim] Simulation mode enabled');
  }

  /** Disable simulation mode. */
  function disable() {
    stop();
    _enabled = false;
    window.EDG_SIMULATION = false;
    _showPanel(false);
    console.log('[Sim] Simulation mode disabled');
  }

  function isEnabled() { return _enabled; }
  function isRunning() { return _running && !_paused; }

  /**
   * Begin the simulation walk.
   * Must be called after routeData is loaded (routeData.segments must be set).
   * @param {object} routeData - from hubConnection.on('RouteLoaded')
   * @param {Function} onTick  - called each tick with position data
   * @param {Function} onDone  - called when destination is reached
   */
  function start(routeData, onTick, onDone) {
    if (!_enabled) return;
    if (_running) stop();

    _onTick = onTick;
    _onDone = onDone;

    _waypoints = _flattenRoute(routeData);
    if (_waypoints.length < 2) {
      console.warn('[Sim] Cannot start — route has fewer than 2 waypoints');
      return;
    }

    _totalRouteM = _waypoints[_waypoints.length - 1].cumulativeM;
    _travelledM  = 0;
    _paused      = false;
    _running     = true;

    // Init car icon on map
    _initCarMarker(_waypoints[0].lat, _waypoints[0].lng);
    _simLaneCount = routeData.segments?.[0]?.laneCount ?? 2;
    _simLane = Math.max(1, _simLaneCount - 1);

    _updateProgressBar();
    _updateSpeedDisplay();
    _tickTimer = setInterval(_tick, TICK_MS);

    console.log('[Sim] Started — route length:', Math.round(_totalRouteM), 'm,', _waypoints.length, 'waypoints');
    _emit('▶ Simulation running — ' + Math.round(_totalRouteM / 1000 * 10) / 10 + ' km to go');
  }

  function pause() {
    if (!_running || _paused) return;
    _paused = true;
    _emit('⏸ Paused');
    _updateControlStates();
  }

  function resume() {
    if (!_running || !_paused) return;
    _paused = false;
    _emit('▶ Resumed');
    _updateControlStates();
  }

  function stop() {
    _running = false;
    _paused  = false;
    if (_tickTimer) { clearInterval(_tickTimer); _tickTimer = null; }
    if (_carMarker) { _carMarker.remove(); _carMarker = null; }
    _waypoints = [];
    _travelledM = 0;
    console.log('[Sim] Stopped');
  }

  /** Jump forward by metres along the route. */
  function skipAhead(metres) {
    _travelledM = Math.min(_travelledM + metres, _totalRouteM - 1);
    _emit(`⏭ Skipped +${metres}m`);
  }

  /** Change simulation speed by a delta (positive = faster). */
  function adjustSpeed(deltaKmh) {
    _simSpeedKmh = Math.max(MIN_SPEED_KMH, Math.min(MAX_SPEED_KMH, _simSpeedKmh + deltaKmh));
    _updateSpeedDisplay();
  }

  function setSpeed(kmh) {
    _simSpeedKmh = Math.max(MIN_SPEED_KMH, Math.min(MAX_SPEED_KMH, kmh));
    _updateSpeedDisplay();
  }

  /** Steer: nudges bearing by ±degrees (cosmetic, route still followed) */
  function steer(direction) {
    // Direction: 'left' | 'right'
    // Bearing cosmetically shifts ±15° for 1 second, then snaps back
    const delta = direction === 'left' ? -15 : 15;
    _simBearing = (_simBearing + delta + 360) % 360;
    _updateCarIcon();
    setTimeout(() => {
      // Snap back to route bearing after 1s
      const pos = _getPositionAt(_travelledM);
      if (pos) _simBearing = _computeBearing(pos, _getPositionAt(_travelledM + 5));
      _updateCarIcon();
    }, 1000);
  }

  /** Change lane (0 = leftmost). Updates hub immediately. */
  function changeLane(direction) {
    const delta = direction === 'left' ? -1 : 1;
    const newLane = Math.max(0, Math.min(_simLaneCount - 1, _simLane + delta));
    if (newLane === _simLane) return;
    _simLane = newLane;
    _emit(`🚗 Lane → ${_simLane + 1} of ${_simLaneCount}`);

    // Notify hub exactly as real driver does
    if (typeof hubConnection !== 'undefined' &&
        hubConnection?.state === signalR.HubConnectionState.Connected) {
      hubConnection.invoke('UpdateLane', _simLane).catch(() => {});
    }
    // Update lane buttons in existing UI
    if (typeof updateLaneButtons === 'function') updateLaneButtons(_simLaneCount, _simLane);
  }

  /** Force a close following distance for testing alerts. */
  function forceCloseGap() {
    _forcedDistanceM = CLOSE_GAP_M;
    _emit('⚠️ Close gap forced — watch for alerts');
    setTimeout(() => { _forcedDistanceM = null; }, 5000);
  }

  function clearForcedGap() { _forcedDistanceM = null; }

  // ═══════════════════════════════════════════════════════════
  // Core tick — called every TICK_MS
  // ═══════════════════════════════════════════════════════════

  function _tick() {
    if (_paused || !_running) return;

    // Advance distance based on current speed
    const stepM = (_simSpeedKmh / 3.6) * (TICK_MS / 1000);
    _travelledM = Math.min(_travelledM + stepM, _totalRouteM);

    const pos = _getPositionAt(_travelledM);
    if (!pos) return;

    // Compute bearing from previous tick
    if (_prevLat !== null) {
      _simBearing = _computeBearing({ lat: _prevLat, lng: _prevLng }, pos);
    }
    _prevLat = pos.lat;
    _prevLng = pos.lng;

    // Synthesise following distance
    const autoDistM = _simSpeedKmh * 0.7 * (1 + (Math.random() - 0.5) * 2 * DIST_NOISE_PCT);
    const distM = _forcedDistanceM ?? autoDistM;

    // Update car icon on map
    _updateCarMarker(pos.lat, pos.lng);

    // Update progress bar
    _updateProgressBar();

    // Fire callback → highway-algorithm.js wires this to hubConnection.invoke
    if (_onTick) {
      _onTick(pos.lat, pos.lng, _simSpeedKmh, _simBearing, _travelledM, _totalRouteM, distM);
    }

    // Check arrival (within 30m of destination)
    if (_totalRouteM - _travelledM < 30) {
      _running = false;
      clearInterval(_tickTimer);
      _tickTimer = null;
      _emit('🏁 Destination reached!');
      if (_onDone) _onDone();
    }
  }

  // ═══════════════════════════════════════════════════════════
  // Route geometry
  // ═══════════════════════════════════════════════════════════

  /** Flatten RouteGraph segments into a flat waypoint array with cumulative distance. */
  function _flattenRoute(routeData) {
    const pts = [];
    let cumM = 0;

    if (!routeData?.segments?.length) return pts;

    for (const seg of routeData.segments) {
      const start = seg.startCoord ?? seg.StartCoord;
      const end   = seg.endCoord   ?? seg.EndCoord;
      if (!start || !end) continue;

      if (pts.length === 0) {
        pts.push({ lat: start.lat ?? start.Lat, lng: start.lng ?? start.Lng, cumulativeM: 0 });
      }

      // Interpolate intermediate points every ~20m for smooth car movement
      const segDistM = seg.distanceMetres ?? seg.DistanceMetres ?? _haversine(
        start.lat ?? start.Lat, start.lng ?? start.Lng,
        end.lat   ?? end.Lat,   end.lng   ?? end.Lng
      );
      const steps = Math.max(1, Math.round(segDistM / 20));

      for (let i = 1; i <= steps; i++) {
        const t = i / steps;
        const lat = (start.lat ?? start.Lat) + t * ((end.lat ?? end.Lat) - (start.lat ?? start.Lat));
        const lng = (start.lng ?? start.Lng) + t * ((end.lng ?? end.Lng) - (start.lng ?? start.Lng));
        cumM += segDistM / steps;
        pts.push({ lat, lng, cumulativeM: cumM });
      }
    }

    return pts;
  }

  /** Get interpolated {lat, lng} at a given travelled distance along the route. */
  function _getPositionAt(targetM) {
    if (!_waypoints.length) return null;
    if (targetM <= 0) return _waypoints[0];
    if (targetM >= _totalRouteM) return _waypoints[_waypoints.length - 1];

    // Binary search for the segment containing targetM
    let lo = 0, hi = _waypoints.length - 1;
    while (lo < hi - 1) {
      const mid = Math.floor((lo + hi) / 2);
      if (_waypoints[mid].cumulativeM <= targetM) lo = mid;
      else hi = mid;
    }

    const a = _waypoints[lo];
    const b = _waypoints[hi];
    const segLen = b.cumulativeM - a.cumulativeM;
    if (segLen === 0) return a;

    const t = (targetM - a.cumulativeM) / segLen;
    return {
      lat: a.lat + t * (b.lat - a.lat),
      lng: a.lng + t * (b.lng - a.lng)
    };
  }

  function _haversine(lat1, lng1, lat2, lng2) {
    const R = 6371000;
    const dLat = (lat2 - lat1) * Math.PI / 180;
    const dLng = (lng2 - lng1) * Math.PI / 180;
    const a = Math.sin(dLat/2)**2 +
              Math.cos(lat1*Math.PI/180) * Math.cos(lat2*Math.PI/180) * Math.sin(dLng/2)**2;
    return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1-a));
  }

  function _computeBearing(from, to) {
    if (!from || !to) return 0;
    const dLng = (to.lng - from.lng) * Math.PI / 180;
    const lat1 = from.lat * Math.PI / 180;
    const lat2 = to.lat   * Math.PI / 180;
    const y = Math.sin(dLng) * Math.cos(lat2);
    const x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLng);
    return ((Math.atan2(y, x) * 180 / Math.PI) + 360) % 360;
  }

  // ═══════════════════════════════════════════════════════════
  // Leaflet car marker
  // ═══════════════════════════════════════════════════════════

  function _initCarMarker(lat, lng) {
    // Requires MapController to have initialised Leaflet map as window._map
    const map = window._simMap ?? window._map ?? null;
    if (!map) { console.warn('[Sim] No Leaflet map found on window._map or window._simMap'); return; }

    _carIcon = L.divIcon({
      className: '',
      html: '<div class="edg-sim-car" id="simCarIcon">🚗</div>',
      iconSize: [32, 32],
      iconAnchor: [16, 16]
    });

    _carMarker = L.marker([lat, lng], { icon: _carIcon, zIndexOffset: 1000 }).addTo(map);
  }

  function _updateCarMarker(lat, lng) {
    if (!_carMarker) return;
    _carMarker.setLatLng([lat, lng]);
    _updateCarIcon();
    // Pan map to follow car
    const map = window._simMap ?? window._map ?? null;
    if (map) map.panTo([lat, lng], { animate: true, duration: 0.4 });
  }

  function _updateCarIcon() {
    const el = document.getElementById('simCarIcon');
    if (el) el.style.transform = `rotate(${_simBearing}deg)`;
  }

  // ═══════════════════════════════════════════════════════════
  // Panel UI helpers
  // ═══════════════════════════════════════════════════════════

  function _showPanel(visible) {
    const panel = document.getElementById('simPanel');
    if (panel) panel.style.display = visible ? '' : 'none';
    const toggle = document.getElementById('simToggle');
    if (toggle) toggle.checked = visible;
  }

  function _updateSpeedDisplay() {
    const el = document.getElementById('simSpeedValue');
    if (el) el.textContent = Math.round(_simSpeedKmh) + ' km/h';
  }

  function _updateProgressBar() {
    const pct = _totalRouteM > 0 ? (_travelledM / _totalRouteM * 100) : 0;
    const bar = document.getElementById('simProgressBar');
    if (bar) {
      bar.style.width = pct.toFixed(1) + '%';
      bar.setAttribute('aria-valuenow', pct.toFixed(0));
    }
    const label = document.getElementById('simProgressLabel');
    if (label) {
      const remaining = Math.max(0, _totalRouteM - _travelledM);
      label.textContent = remaining >= 1000
        ? (remaining / 1000).toFixed(1) + ' km to go'
        : Math.round(remaining) + ' m to go';
    }
  }

  function _updateControlStates() {
    const btnPause  = document.getElementById('simBtnPause');
    const btnResume = document.getElementById('simBtnResume');
    if (btnPause)  btnPause.disabled  = _paused;
    if (btnResume) btnResume.disabled = !_paused;
  }

  function _emit(msg) {
    const el = document.getElementById('simStatus');
    if (el) el.textContent = msg;
    if (_onAlert) _onAlert(msg);
    console.log('[Sim]', msg);
  }

  // ── Check for ?sim=1 on page load ─────────────────────────
  if (new URLSearchParams(window.location.search).get('sim') === '1') {
    window.EDG_SIMULATION = true;
  }

  // ── Expose public API ──────────────────────────────────────
  return {
    enable, disable, isEnabled, isRunning,
    start, pause, resume, stop, skipAhead,
    adjustSpeed, setSpeed,
    steer, changeLane,
    forceCloseGap, clearForcedGap,
    SPEED_STEP_KMH
  };

})();
