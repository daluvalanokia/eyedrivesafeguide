# 🚗 Drive Simulation Module — EyeDriveSafeGuide
**Feature branch:** `feature/simulation-module`  
**Date:** June 11, 2026

---

## Strategy — Why Hybrid Client-Driven

The simulation engine runs entirely in the browser and feeds synthetic GPS coordinates into the **exact same SignalR `UpdatePosition` call** that the real GPS sensor uses.

This means:
- Every existing server algorithm fires identically (merge, lane, exit, speed, following distance)
- Zero server code changes
- Alerts, route tracking, session summary all work
- The car icon is drawn on the existing Leaflet map

---

## Architecture

```
SimulationEngine (simulation.js)
  │
  │  walks routeData.segments geometry
  │  synthesises lat/lng/speed
  │
  ▼
hubConnection.invoke('UpdatePosition', lat, lng, kmh, 0, 0, simDistanceM)
  │
  ▼  (identical to real GPS path)
DriveHub → MergeAlgorithm, LaneDisciplineAlgorithm, ExitAlgorithm, SpeedAlert, FollowingDistance
  │
  ▼
Clients.Caller.SendAsync('Alert' / 'PositionAck' / 'FollowingAlert' ...)
  │
  ▼
AlertSystem.show() + MapController.updatePosition() + HUD updates
```

---

## Simulation Control Panel

Shown **next to the destination row**, visible only when simulation is enabled and drive is active.

```
┌─────────────────────────────────────────────┐
│  🚗 SIMULATION MODE                    [off] │
├─────────────────────────────────────────────┤
│  Speed:  [−5]  [ 45 km/h ]  [+5]           │
│  ─────────────────────────────────────────  │
│  Steer:  [◀ Turn L]        [Turn R ▶]       │
│  Lane:   [← Lane L]        [Lane R →]       │
│  ─────────────────────────────────────────  │
│  [⏸ Pause]   [▶ Resume]   [⏭ +500m]        │
└─────────────────────────────────────────────┘
```

---

## Simulation State Machine

```
IDLE → RUNNING → PAUSED → RUNNING → COMPLETED
                    ↓
                  STOPPED (user hits Stop Drive)
```

---

## Variable: window.EDG_SIMULATION

```js
window.EDG_SIMULATION = false; // Default OFF
```

Controlled via toggle in the settings panel AND a `?sim=1` query string for dev testing.

---

## Route Walking Algorithm

1. Flatten `routeData.segments` into an array of `[lat, lng]` waypoints
2. Interpolate between waypoints at the current `simSpeedKmh`
3. Every `tickMs` (default 500 ms), advance `distanceTravelledM` by `(simSpeedKmh / 3.6) * (tickMs/1000)`
4. Find which waypoint segment the position falls on using cumulative distance
5. Interpolate exact `[lat, lng]` within that segment
6. Compute `bearing` from previous to current point (for car icon rotation)
7. Inject into `hubConnection.invoke('UpdatePosition', ...)`

---

## Car Icon

- Leaflet `L.divIcon` with a 🚗 emoji rotated to the current bearing
- CSS `transform: rotate(${bearing}deg)` updated every tick
- Separate from the user's real position marker

---

## Synthetic Following Distance

When in simulation, `frontDistanceM` is synthesised:
- Base: `simSpeedKmh * 0.7` (proportional — faster = further ahead)
- Randomised ±15% each tick (simulates real-world variation)
- Manually reducible via the **"Close Gap"** test button (fires warning/critical alerts)

---

## Files

| File | Purpose |
|------|---------|
| `wwwroot/js/simulation.js` | Full simulation engine |
| `Views/Navigation/_SimPanel.cshtml` | Partial view — control panel HTML |
| `Views/Navigation/Index.cshtml` (patch) | Toggle + panel wiring |
| `Controllers/Api/SimulationController.cs` | Optional: server-side sim config persistence |
