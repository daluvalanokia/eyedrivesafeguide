// highway-algorithm.js — Drive page orchestration: SignalR + sensors + map + alerts

let hubConnection = null;
let driveMode = 'destination';
let isDriving = false;
let accelMag = 0;
let currentDbLevel = 0;
let accelStopFn = null;
let sessionStats = { speed: [], distraction: 0, backing: 0, lane: 0, merge: 0, exit: 0 };
let nightModeActive = false;
let routeData = null;
let currentLane = 1;
let currentSegmentLaneCount = 2;
let currentSpeedLimitKmh = null;

// ── Mode toggle ──────────────────────────────────────────────────────────────
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

// ── Permissions ──────────────────────────────────────────────────────────────
async function requestPermissions() {
    const res = document.getElementById('permResults');
    res.innerHTML = '<div class="text-muted small">Requesting…</div>';
    const perms = await SensorMonitor.requestPermissions();
    let html = '<ul class="list-unstyled mt-2">';
    html += `<li>${perms.gps ? '✅' : '❌'} Location</li>`;
    html += `<li>${perms.mic ? '✅' : '❌'} Microphone</li>`;
    html += `<li>${perms.camera ? '✅' : '❌'} Camera (backing detection)</li>`;
    html += '</ul>';
    if (!perms.gps) html += '<p class="text-warning small">Location required for navigation. Enable it in browser settings.</p>';
    res.innerHTML = html;
}

// ── SignalR setup ────────────────────────────────────────────────────────────
function buildHub() {
    hubConnection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/drive')
        .withAutomaticReconnect()
        .build();

    hubConnection.on('SessionStarted', id => {
        setStatus(`Session started`);
    });

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
            if (hubConnection?.state === signalR.HubConnectionState.Connected) {
                hubConnection.invoke('UpdateLane', currentLane).catch(() => {});
            }
        }
    });

    hubConnection.on('Alert', alert => {
        AlertSystem.show(alert);
        if (alert.type === 'speed-red' || alert.type === 'speed-yellow') sessionStats.speed.push(alert);
        if (alert.type === 'distraction') sessionStats.distraction++;
        if (alert.type === 'backing') sessionStats.backing++;
        if (alert.type === 'lane') sessionStats.lane++;
        if (alert.type === 'merge') sessionStats.merge++;
        if (alert.type === 'exit') sessionStats.exit++;
    });

    hubConnection.on('MissedExit', data => {
        AlertSystem.show({ type: 'exit', message: data.message, severity: 'danger', blindSpotHold: false });
        // Trigger reroute if destination is set
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

    hubConnection.on('SessionEnded', summary => {
        showSummary(summary);
    });
}

// ── Start / Stop ─────────────────────────────────────────────────────────────
async function startDrive() {
    if (isDriving) return;

    document.getElementById('btnStart').style.display = 'none';
    document.getElementById('btnStop').style.display = '';

    sessionStats = { speed: [], distraction: 0, backing: 0, lane: 0, merge: 0, exit: 0 };
    AlertSystem.clear();
    MapController.init();
    navigator.geolocation.getCurrentPosition(
        p => checkNightMode(p.coords.latitude, p.coords.longitude),
        () => checkNightMode(null, null),
        { timeout: 3000, maximumAge: 60000 }
    );

    buildHub();
    await hubConnection.start();

    const sel = document.getElementById('destinationSelect');
    const destAddr = sel?.options[sel.selectedIndex]?.dataset?.addr || null;
    await hubConnection.invoke('StartSession', driveMode, destAddr);

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
            await hubConnection.invoke('UpdatePosition', lat, lng, kmh, accelMag, currentDbLevel).catch(() => {});
        }
    }, err => {
        showPermWarning(`GPS: ${err}`);
    });

    accelStopFn = SensorMonitor.startAccelerometer(val => { accelMag = val; });

    await SensorMonitor.startMicrophone(db => {
        currentDbLevel = db;
        updateDbMeter(db);
    });

    await SensorMonitor.startBacking(() => {
        if (hubConnection?.state === signalR.HubConnectionState.Connected) {
            hubConnection.invoke('BackingAlert').catch(() => {});
        }
    });

    if (driveMode === 'destination') {
        const destSel = document.getElementById('destinationSelect');
        if (destSel?.value) {
            await loadRoute();
        }
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
        // fallback: use map center
        hubConnection.invoke('LoadRoute', 40.7128, -74.006, destLat, destLng).catch(() => {});
    });
}

async function stopDrive() {
    if (!isDriving) return;
    isDriving = false;

    document.getElementById('btnStop').style.display = 'none';
    document.getElementById('btnStart').style.display = '';

    SensorMonitor.stopGps();
    SensorMonitor.stopMicrophone();
    SensorMonitor.stopBacking();
    if (accelStopFn) { accelStopFn(); accelStopFn = null; }

    if (hubConnection?.state === signalR.HubConnectionState.Connected) {
        await hubConnection.invoke('EndSession').catch(() => {});
        await hubConnection.stop();
    }
    MapController.clearRoute();
    currentLane = 1;
    currentSegmentLaneCount = 2;
    currentSpeedLimitKmh = null;
    const ctrl = document.getElementById('laneControl');
    if (ctrl) ctrl.style.display = 'none';
    setStatus('Drive ended.');
}

// ── UI helpers ────────────────────────────────────────────────────────────────
function updateSpeedDisplay(kmh, speedLimit) {
    const el = document.getElementById('speedDisplay');
    if (!el) return;
    el.textContent = Math.round(kmh);

    const badge = document.getElementById('speedLimitBadge');
    if (badge) {
        badge.textContent = speedLimit != null ? `limit ${speedLimit} km/h` : 'limit —';
    }

    const settings = window.EDG_SETTINGS || {};
    const night = nightModeActive ? -8 : 0;
    const baseLimit = speedLimit ?? 80;
    const yellow = baseLimit + (settings.yellowThreshold ?? 10) + night;
    const red = baseLimit + (settings.redThreshold ?? 15) + night;

    el.classList.remove('text-white', 'text-warning', 'text-danger');
    if (kmh >= red) el.classList.add('text-danger');
    else if (kmh >= yellow) el.classList.add('text-warning');
    else el.classList.add('text-white');
}

function updateDbMeter(db) {
    const el = document.getElementById('dbMeter');
    const label = document.getElementById('dbValue');
    if (!el || !label) return;
    const pct = Math.min(100, (db / 100) * 100);
    el.style.width = pct + '%';
    label.textContent = db.toFixed(0) + ' dB';
    const threshold = (window.EDG_SETTINGS?.distractionDbLevel) || 60;
    el.className = 'progress-bar ' + (db > threshold ? 'bg-danger' : db > threshold * 0.85 ? 'bg-warning' : 'bg-success');
}

function findNearestSegment(lat, lng) {
    if (!routeData?.segments?.length) return null;
    let best = null, bestDist = Infinity;
    routeData.segments.forEach(seg => {
        const sc = seg.startCoord, ec = seg.endCoord;
        const d = Math.min(
            Math.hypot(lat - sc.lat, lng - sc.lng),
            Math.hypot(lat - ec.lat, lng - ec.lng)
        );
        if (d < bestDist) { bestDist = d; best = seg; }
    });
    return best;
}

function updateLaneButtons(laneCount) {
    const ctrl = document.getElementById('laneControl');
    const container = document.getElementById('laneButtons');
    if (!ctrl || !container) return;
    if (laneCount <= 1) { ctrl.style.display = 'none'; return; }
    ctrl.style.display = '';
    container.innerHTML = '';
    for (let i = 0; i < laneCount; i++) {
        const label = i === 0 ? '◀ Passing' : i === laneCount - 1 ? 'Right ▶' : `Lane ${i + 1}`;
        const btn = document.createElement('button');
        btn.className = `btn btn-sm ${i === currentLane ? 'btn-primary' : 'btn-outline-secondary'}`;
        btn.textContent = label;
        btn.onclick = () => selectLane(i, laneCount);
        container.appendChild(btn);
    }
}

function selectLane(index, laneCount) {
    currentLane = index;
    updateLaneButtons(laneCount);
    if (hubConnection?.state === signalR.HubConnectionState.Connected) {
        hubConnection.invoke('UpdateLane', index).catch(() => {});
    }
}

function setStatus(msg) {
    const el = document.getElementById('statusLine');
    if (el) el.textContent = msg;
}

function showPermWarning(msg) {
    const w = document.getElementById('permWarning');
    const t = document.getElementById('permWarningText');
    if (w && t) { t.textContent = msg; w.style.display = ''; }
}

async function checkNightMode(lat, lng) {
    if (!(window.EDG_SETTINGS?.weatherNightMode)) return;

    if (lat != null && lng != null) {
        try {
            await checkNightModeWithPosition(lat, lng);
            return;
        } catch (_) {}
    }
    const h = new Date().getHours();
    nightModeActive = h >= 20 || h < 6;
}

async function checkNightModeWithPosition(lat, lng) {
    const now = new Date();
    const rad = Math.PI / 180;

    const jd = now.getTime() / 86400000 + 2440587.5;
    const n = jd - 2451545.0;
    const L = ((280.46 + 0.9856474 * n) % 360 + 360) % 360;
    const g = ((357.528 + 0.9856003 * n) % 360) * rad;
    const lambda = (L + 1.915 * Math.sin(g) + 0.02 * Math.sin(2 * g)) * rad;
    const decl = Math.asin(0.39779 * Math.sin(lambda));
    const latRad = lat * rad;
    const cosH = (Math.cos(90.833 * rad) - Math.sin(decl) * Math.sin(latRad)) /
                 (Math.cos(decl) * Math.cos(latRad));

    let isNight;
    if (cosH < -1) {
        isNight = false;
    } else if (cosH > 1) {
        isNight = true;
    } else {
        const H = Math.acos(cosH) * 180 / Math.PI;
        const eqTime = (L - (new Date(now.getFullYear(), 0, 0).getTime() === 0 ? 0 : 0));
        const transitUTC = 12 - lng / 15;
        const sunriseUTC = transitUTC - H / 15;
        const sunsetUTC  = transitUTC + H / 15;
        const hourUTC = now.getUTCHours() + now.getUTCMinutes() / 60;
        isNight = hourUTC < sunriseUTC || hourUTC > sunsetUTC;
    }

    let hasWeatherAlert = false;
    try {
        const url = `https://api.open-meteo.com/v1/forecast?latitude=${lat.toFixed(4)}&longitude=${lng.toFixed(4)}&current=precipitation,weather_code&forecast_days=1&timezone=auto`;
        const resp = await fetch(url, { signal: AbortSignal.timeout(4000) });
        if (resp.ok) {
            const data = await resp.json();
            const precip = data?.current?.precipitation ?? 0;
            const code  = data?.current?.weather_code ?? 0;
            if (precip > 0.1 || (code >= 51 && code <= 99)) {
                hasWeatherAlert = true;
            }
        }
    } catch (_) {}

    nightModeActive = isNight || hasWeatherAlert;
    if (hubConnection?.state === signalR.HubConnectionState.Connected) {
        hubConnection.invoke('SetDrivingConditions', nightModeActive).catch(() => {});
    }
}

function showSummary(s) {
    const body = document.getElementById('summaryBody');
    if (!body) return;

    const score = s.speedConsistencyScore;
    const scoreColor = score >= 80 ? 'success' : score >= 60 ? 'warning' : 'danger';
    const scoreEmoji = score >= 80 ? '🌟' : score >= 60 ? '👍' : '⚠️';

    body.innerHTML = `
        <div class="row g-3 text-center">
            <div class="col-6">
                <div class="fw-bold display-6">${s.totalDistanceKm}</div>
                <div class="text-muted small">km driven</div>
            </div>
            <div class="col-6">
                <div class="fw-bold display-6">${s.averageSpeedKmh}</div>
                <div class="text-muted small">avg km/h</div>
            </div>
            <div class="col-12">
                <div class="fw-bold text-${scoreColor} display-5">${scoreEmoji} ${score.toFixed(0)}</div>
                <div class="text-muted small">Speed consistency score (higher = smoother)</div>
            </div>
        </div>
        <hr/>
        <div class="row g-2 text-center small">
            <div class="col-4"><div class="badge bg-danger w-100 py-2">⚡ ${s.speedAlertCount} speed</div></div>
            <div class="col-4"><div class="badge bg-warning text-dark w-100 py-2">🔊 ${s.distractionAlertCount} distraction</div></div>
            <div class="col-4"><div class="badge bg-secondary w-100 py-2">⚠️ ${s.backingAlertCount} backing</div></div>
            <div class="col-4"><div class="badge bg-primary w-100 py-2">🛣️ ${s.laneChangeAlertCount} lane</div></div>
            <div class="col-4"><div class="badge bg-info text-dark w-100 py-2">🔀 ${s.mergeAlertCount} merge</div></div>
            <div class="col-4"><div class="badge bg-success w-100 py-2">🚪 ${s.exitAlertCount} exit</div></div>
        </div>
        <div class="text-muted text-center small mt-2">Duration: ${s.durationMinutes} min</div>
    `;

    new bootstrap.Modal(document.getElementById('summaryModal')).show();
}

// ── Init ──────────────────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
    MapController.init();
    setMode('destination');

    const firstVisit = !localStorage.getItem('edg_perms_asked');
    if (firstVisit) {
        localStorage.setItem('edg_perms_asked', '1');
        new bootstrap.Modal(document.getElementById('permModal')).show();
    }
});
