// highway-algorithm.js — Drive page orchestration: SignalR + sensors + map + alerts
// Merged: following distance wiring from highway-algorithm-distance-patch.js

let hubConnection      = null;
let driveMode          = 'destination';
let isDriving          = false;
let accelMag           = 0;
let currentDbLevel     = 0;
let accelStopFn        = null;
let sessionStats       = { speed: [], distraction: 0, backing: 0, lane: 0, merge: 0, exit: 0 };
let nightModeActive    = false;
let routeData          = null;
let currentLane        = 1;
let currentSegmentLaneCount = 2;
let currentSpeedLimitKmh    = null;

// ── Following distance state (merged from distance-patch) ────
let frontDistanceM  = null;   // current distance to vehicle ahead (metres)
let distanceSource  = null;   // 'camera' | 'serial' | null

// ── Mode toggle ──────────────────────────────────────────────
function setMode(mode) {
    driveMode = mode;
    document.getElementById('btnDestMode').classList.toggle('active-mode', mode === 'destination');
    document.getElementById('btnJustDrive').classList.toggle('active-mode', mode === 'justdrive');
    document.getElementById('btnDestMode').classList.toggle('btn-primary', mode === 'destination');
    document.getElementById('btnDestMode').classList.toggle('btn-outline-secondary', mode !== 'destination');
    document.getElementById('btnJustDrive').classList.toggle('btn-primary', mode === 'justdrive');
    document.getElementById('btnJustDrive').classList.toggle('btn-outline-secondary', mode !== 'justdrive');
    document.getElementById('destinationRow').style.display = mode === 'destination' ? '' : 'none';
}

// ── Permissions ──────────────────────────────────────────────
async function requestPermissions() {
    const res = document.getElementById('permResults');
    res.innerHTML = '<div class="text-muted small">Requesting…</div>';
    const perms = await SensorMonitor.requestPermissions();
    let html = '<ul class="list-unstyled mt-2">';
    html += `<li>${perms.gps    ? '✅' : '❌'} Location</li>`;
    html += `<li>${perms.mic    ? '✅' : '❌'} Microphone</li>`;
    html += `<li>${perms.camera ? '✅' : '❌'} Camera (backing detection)</li>`;
    html += '</ul>';
    if (!perms.gps) html += '<p class="text-warning small">Location required for navigation. Enable it in browser settings.</p>';
    res.innerHTML = html;
}

// ── SignalR setup ────────────────────────────────────────────
function buildHub() {
    hubConnection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/drive')
        .withAutomaticReconnect()
        .build();

    hubConnection.on('SessionStarted', () => setStatus('Session started'));

    hubConnection.on('RouteLoaded', data => {
        routeData = data;
        MapController.drawRoute(data);
        const km = (data.totalDistanceMetres / 1000).toFixed(1);
        setStatus(`Route loaded — ${km} km, ${data.segmentCount} segments`);
        if (data.errorMessage) setStatus(`ℹ️ ${data.errorMessage}`);
        if (data.segments?.length > 0) {
            currentSegmentLaneCount = data.segments[0].laneCount || 2;
            currentLane = Math.max(1, currentSegmentLaneCount - 1);
            updateLaneButtons(currentSegmentLaneCount);
            if (hubConnection?.state === signalR.HubConnectionState.Connected)
                hubConnection.invoke('UpdateLane', currentLane).catch(() => {});
        }
        // If simulation is enabled, hand route to SimulationEngine
        if (window.SimulationEngine?.isEnabled()) {
            document.getElementById('simPanel').style.display = '';
            SimulationEngine.start(data, _simOnTick, _simOnStop);
        }
    });

    hubConnection.on('Alert', alert => {
        AlertSystem.show(alert);
        if (alert.type === 'speed-red' || alert.type === 'speed-yellow') sessionStats.speed.push(alert);
        if (alert.type === 'distraction') sessionStats.distraction++;
        if (alert.type === 'backing')     sessionStats.backing++;
        if (alert.type === 'lane')        sessionStats.lane++;
        if (alert.type === 'merge')       sessionStats.merge++;
        if (alert.type === 'exit')        sessionStats.exit++;
    });

    // ── Following distance hub events (merged from patch) ────
    hubConnection.on('FollowingAlert', data => {
        AlertSystem.show(data);
        _updateDistanceHud(data.currentDistanceM, data.recommendedDistanceM, data.level);
    });

    hubConnection.on('FollowingDistanceSuppressed', data => {
        _updateDistanceHud(data.currentDistanceM, null, 'suppressed');
    });

    hubConnection.on('MissedExit', data => {
        AlertSystem.show({ type: 'exit', message: data.message, severity: 'danger', blindSpotHold: false });
        const sel = document.getElementById('destinationSelect');
        if (sel?.value && routeData) {
            setStatus('Recalculating route…');
            setTimeout(() => loadRoute(), 1500);
        }
    });

    hubConnection.on('PositionAck', data => {
        if (data.speedLimitKmh != null) currentSpeedLimitKmh = data.speedLimitKmh;
        updateSpeedDisplay(data.speedKmh, currentSpeedLimitKmh);
    });

    hubConnection.on('SessionEnded', summary => showSummary(summary));
}

// ── Start / Stop ─────────────────────────────────────────────
async function startDrive() {
    if (isDriving) return;

    document.getElementById('btnStart').style.display = 'none';
    document.getElementById('btnStop').style.display  = '';

    sessionStats = { speed: [], distraction: 0, backing: 0, lane: 0, merge: 0, exit: 0 };
    AlertSystem.clear();
    MapController.init();
    navigator.geolocation.getCurrentPosition(
        p  => checkNightMode(p.coords.latitude, p.coords.longitude),
        () => checkNightMode(null, null),
        { timeout: 3000, maximumAge: 60000 }
    );

    buildHub();
    await hubConnection.start();

    const sel      = document.getElementById('destinationSelect');
    const destAddr = sel?.options[sel.selectedIndex]?.dataset?.addr || null;
    await hubConnection.invoke('StartSession', driveMode, destAddr);

    // ── Start DistanceMonitor (merged from patch) ────────────
    await _startDistanceMonitor();

    SensorMonitor.startGps(async (lat, lng, kmh) => {
        MapController.updatePosition(lat, lng);

        if (routeData?.segments?.length > 0) {
            const seg = findNearestSegment(lat, lng) || routeData.segments[0];
            if (seg) {
                currentSpeedLimitKmh = seg.speedLimitKmh ?? null;
                if (seg.laneCount !== currentSegmentLaneCount) {
                    currentSegmentLaneCount = seg.laneCount;
                    updateLaneButtons(seg.laneCount);
                }
            }
        }
        updateSpeedDisplay(kmh, currentSpeedLimitKmh);

        if (hubConnection?.state === signalR.HubConnectionState.Connected) {
            // Pass frontDistanceM as 6th arg (merged from patch)
            await hubConnection.invoke(
                'UpdatePosition', lat, lng, kmh, accelMag, currentDbLevel, frontDistanceM
            ).catch(() => {});
        }
    }, err => showPermWarning(`GPS: ${err}`));

    accelStopFn = SensorMonitor.startAccelerometer(val => { accelMag = val; });

    await SensorMonitor.startMicrophone(db => {
        currentDbLevel = db;
        updateDbMeter(db);
    });

    await SensorMonitor.startBacking(() => {
        if (hubConnection?.state === signalR.HubConnectionState.Connected)
            hubConnection.invoke('BackingAlert').catch(() => {});
    });

    if (driveMode === 'destination') {
        const destSel = document.getElementById('destinationSelect');
        if (destSel?.value) await loadRoute();
    }

    isDriving = true;
    setStatus('Driving…');
}

async function loadRoute() {
    const sel = document.getElementById('destinationSelect');
    const opt = sel?.options[sel.selectedIndex];
    if (!opt?.value) return;

    const destLat = parseFloat(opt.dataset.lat);
    const destLng = parseFloat(opt.dataset.lng);
    if (isNaN(destLat) || isNaN(destLng)) {
        setStatus('ℹ️ Destination has no coordinates — add lat/lng in Settings');
        return;
    }
    setStatus('Loading route…');
    navigator.geolocation.getCurrentPosition(async pos => {
        const { latitude: sLat, longitude: sLng } = pos.coords;
        await hubConnection.invoke('LoadRoute', sLat, sLng, destLat, destLng).catch(() => {});
    }, () => {
        hubConnection.invoke('LoadRoute', 40.7128, -74.006, destLat, destLng).catch(() => {});
    });
}

async function stopDrive() {
    if (!isDriving) return;
    isDriving = false;

    document.getElementById('btnStop').style.display  = 'none';
    document.getElementById('btnStart').style.display = '';

    SensorMonitor.stopGps();
    SensorMonitor.stopMicrophone();
    SensorMonitor.stopBacking();
    if (accelStopFn) { accelStopFn(); accelStopFn = null; }

    // Stop DistanceMonitor (merged from patch)
    _stopDistanceMonitor();

    // Stop simulation if running
    if (window.SimulationEngine?.isRunning()) {
        SimulationEngine.stop();
        const simPanel = document.getElementById('simPanel');
        if (simPanel) simPanel.style.display = 'none';
    }

    if (hubConnection?.state === signalR.HubConnectionState.Connected) {
        await hubConnection.invoke('EndSession').catch(() => {});
        await hubConnection.stop();
    }
    MapController.clearRoute();
    currentLane             = 1;
    currentSegmentLaneCount = 2;
    currentSpeedLimitKmh    = null;
    const ctrl = document.getElementById('laneControl');
    if (ctrl) ctrl.style.display = 'none';
    setStatus('Drive ended.');
}

// ── Following distance helpers (merged from patch) ───────────
async function _startDistanceMonitor() {
    if (typeof DistanceMonitor === 'undefined') return;
    await DistanceMonitor.start(distanceM => { frontDistanceM = distanceM; });
    distanceSource = DistanceMonitor.getSource();
    const sourceEl = document.getElementById('distanceSource');
    if (sourceEl)
        sourceEl.textContent = distanceSource
            ? `Distance sensor: ${distanceSource}`
            : 'Distance sensor: unavailable';
}

function _stopDistanceMonitor() {
    if (typeof DistanceMonitor !== 'undefined') DistanceMonitor.stop();
    frontDistanceM = null;
    _updateDistanceHud(null, null, null);
}

function _updateDistanceHud(currentM, recommendedM, level) {
    const el = document.getElementById('followingDistanceHud');
    if (!el) return;
    if (currentM == null) { el.style.display = 'none'; return; }
    el.style.display = 'block';
    const levelColors = {
        'None':          '#28a745',
        'Advisory':      '#17a2b8',
        'Warning':       '#ffc107',
        'Critical':      '#dc3545',
        'PersonalDrift': '#6f42c1',
        'suppressed':    '#6c757d',
    };
    el.style.borderColor = levelColors[level] || '#6c757d';
    el.querySelector('.dist-current').textContent     = `${currentM.toFixed(0)} m`;
    el.querySelector('.dist-recommended').textContent = recommendedM != null ? ` / ${recommendedM.toFixed(0)} m ideal` : '';
    el.querySelector('.dist-icon').textContent =
        level === 'Critical'   ? '⚠️' :
        level === 'Warning'    ? '🟡' :
        level === 'suppressed' ? '🚦' : '📏';
}

// ── Simulation callbacks (wired from RouteLoaded) ─────────────
async function _simOnTick(lat, lng, kmh, bearing, travelledM, totalM, distM) {
    MapController.updatePosition(lat, lng);
    updateSpeedDisplay(kmh, currentSpeedLimitKmh);
    if (hubConnection?.state === signalR.HubConnectionState.Connected) {
        await hubConnection.invoke(
            'UpdatePosition', lat, lng, kmh, 0, 0, distM ?? null
        ).catch(() => {});
    }
    // Progress bar update
    const pb = document.getElementById('simProgressBar');
    if (pb && totalM > 0) pb.style.width = Math.min(100, (travelledM / totalM) * 100).toFixed(1) + '%';
}

function _simOnStop() {
    const simPanel = document.getElementById('simPanel');
    if (simPanel) simPanel.style.display = 'none';
}

// ── UI helpers ────────────────────────────────────────────────
function updateSpeedDisplay(kmh, speedLimit) {
    const el = document.getElementById('speedDisplay');
    if (!el) return;
    el.textContent = Math.round(kmh);
    const badge = document.getElementById('speedLimitBadge');
    if (badge) badge.textContent = speedLimit != null ? `limit ${speedLimit} km/h` : 'limit —';
    const settings  = window.EDG_SETTINGS || {};
    const night     = nightModeActive ? -8 : 0;
    const baseLimit = speedLimit ?? 80;
    const yellow    = baseLimit + (settings.yellowThreshold ?? 10) + night;
    const red       = baseLimit + (settings.redThreshold   ?? 15) + night;
    el.classList.remove('text-white', 'text-warning', 'text-danger');
    if (kmh >= red)    el.classList.add('text-danger');
    else if (kmh >= yellow) el.classList.add('text-warning');
    else               el.classList.add('text-white');
}

function updateDbMeter(db) {
    const el    = document.getElementById('dbMeter');
    const label = document.getElementById('dbValue');
    if (!el || !label) return;
    el.style.width   = Math.min(100, (db / 100) * 100) + '%';
    label.textContent = db.toFixed(0) + ' dB';
    const threshold  = window.EDG_SETTINGS?.distractionDbLevel || 60;
    el.className = 'progress-bar ' +
        (db > threshold          ? 'bg-danger'  :
         db > threshold * 0.85   ? 'bg-warning' : 'bg-success');
}

function findNearestSegment(lat, lng) {
    if (!routeData?.segments?.length) return null;
    let best = null, bestDist = Infinity;
    routeData.segments.forEach(seg => {
        const d = Math.hypot(
            (seg.startCoord?.lat ?? 0) - lat,
            (seg.startCoord?.lng ?? 0) - lng
        );
        if (d < bestDist) { bestDist = d; best = seg; }
    });
    return best;
}

function updateLaneButtons(laneCount) {
    const ctrl    = document.getElementById('laneControl');
    const buttons = document.getElementById('laneButtons');
    if (!ctrl || !buttons) return;
    if (laneCount < 2) { ctrl.style.display = 'none'; return; }
    ctrl.style.display = '';
    buttons.innerHTML  = '';
    for (let i = 1; i <= laneCount; i++) {
        const btn = document.createElement('button');
        btn.type      = 'button';
        btn.className = 'btn btn-sm ' + (i === currentLane ? 'btn-primary' : 'btn-outline-secondary');
        btn.textContent = i === 1          ? `Lane ${i} (Right)` :
                          i === laneCount  ? `Lane ${i} (Left)`  : `Lane ${i}`;
        btn.onclick = () => selectLane(i);
        buttons.appendChild(btn);
    }
}

function selectLane(lane) {
    currentLane = lane;
    updateLaneButtons(currentSegmentLaneCount);
    if (hubConnection?.state === signalR.HubConnectionState.Connected)
        hubConnection.invoke('UpdateLane', lane).catch(() => {});
}

function showPermWarning(msg) {
    const el   = document.getElementById('permWarning');
    const text = document.getElementById('permWarningText');
    if (!el || !text) return;
    text.textContent = msg;
    el.style.display = '';
}

function setStatus(msg) {
    const el = document.getElementById('statusLine');
    if (el) el.textContent = msg;
}

async function checkNightMode(lat, lng) {
    if (!window.EDG_SETTINGS?.weatherNightMode) return;
    const hour = new Date().getHours();
    nightModeActive = hour < 6 || hour >= 20;
    if (hubConnection?.state === signalR.HubConnectionState.Connected)
        hubConnection.invoke('SetDrivingConditions', nightModeActive).catch(() => {});
}

function showSummary(summary) {
    const body  = document.getElementById('summaryBody');
    const modal = document.getElementById('summaryModal');
    if (!body) return;
    // Safe DOM construction — no innerHTML with server data
    const rows = [
        ['Distance',    `${summary.totalDistanceKm} km`],
        ['Avg Speed',   `${summary.averageSpeedKmh} km/h`],
        ['Consistency', `${summary.speedConsistencyScore}%`],
        ['Duration',    `${summary.durationMinutes} min`],
        ['Speed alerts',       summary.speedAlertCount],
        ['Distraction alerts', summary.distractionAlertCount],
        ['Lane alerts',        summary.laneChangeAlertCount],
        ['Merge alerts',       summary.mergeAlertCount],
        ['Exit alerts',        summary.exitAlertCount],
    ];
    const table = document.createElement('table');
    table.className = 'table table-sm';
    rows.forEach(([label, value]) => {
        const tr = table.insertRow();
        tr.insertCell().textContent = label;
        const td = tr.insertCell();
        td.textContent = value;
        td.className   = 'text-end fw-semibold';
    });
    body.innerHTML = '';
    body.appendChild(table);
    if (modal) new bootstrap.Modal(modal).show();
}

// ── Init ──────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
    setMode('destination');
    // Show sim panel toggle only if simulation.js is loaded
    const simToggleRow = document.getElementById('simToggleRow');
    if (simToggleRow)
        simToggleRow.style.display = typeof SimulationEngine !== 'undefined' ? '' : 'none';
});
