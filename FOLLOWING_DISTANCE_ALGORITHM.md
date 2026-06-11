# 📏 Following Distance Algorithm — EyeDriveSafeGuide
**Feature branch:** `feature/following-distance-algorithm`  
**Date:** June 11, 2026

---

## Overview

This feature adds an **adaptive following distance monitor** that:

1. Reads forward sensor distance from the front-facing camera (optical flow + time-to-collision estimate) or an ultrasonic/LIDAR feed via a WebSerial/Bluetooth peripheral
2. Stores per-session distance data and computes a **10-ride rolling average** as the driver's personalised baseline
3. Applies a **5-tier speed-band safety envelope** (≤25 / ≤45 / ≤55 / ≤65 / ≤70+ km/h) with physics-derived minimum safe distances
4. Issues **contextual, manageable alerts** — not spam — while suppressing them during traffic jams, stop-and-go, and signal stops

---

## Physics Basis — Speed Band Safe Distances

The algorithm uses the **2-second rule** as a minimum, extended to **3 seconds at highway speeds**, plus a reaction-time buffer of 1.5 s.

```
SafeDistance(m) = (reactionTime + brakeTime) × speed_m_s

brakeTime = speed_m_s / deceleration   (deceleration = 7.0 m/s² dry road)
```

| Speed Band | km/h Range | Min Safe (m) | Warn at (m) | Ideal (m) | Rule-of-thumb |
|-----------|------------|-------------|-------------|-----------|---------------|
| URBAN_LOW  | 0–25       | 7           | 12          | 18        | ≈ 1 car length |
| URBAN_MID  | 26–45      | 14          | 22          | 30        | 2-second rule |
| SUBURBAN   | 46–55      | 20          | 32          | 45        | 2-second rule |
| HIGHWAY    | 56–65      | 28          | 44          | 60        | 3-second rule |
| FREEWAY    | 66–70+     | 35          | 55          | 75        | 3-second rule |

---

## 10-Ride Rolling Average Baseline

- After each highway ride (any session where `isHighway=true` for ≥ 50% of time), the system saves:
  - `averageDistanceM` — mean distance maintained to front vehicle
  - `speedBand` — dominant speed band for that segment
  - `alertCount` — how many following-distance alerts were fired
- The last **10 highway rides** per speed band are stored in the `FollowingDistanceHistory` table
- A **personalised baseline** `baselineM` = average of the last 10 ride averages for that band
- If the driver's current distance **drops below 80% of their own baseline**, a gentle awareness alert fires ("You're closer than your usual distance")
- This avoids nagging a driver who naturally maintains 60 m at highway speed, but catches someone who normally sits at 50 m suddenly crowding at 25 m

---

## Traffic Jam & Signal Suppression

The alert is suppressed when **all three conditions are met**:

```
1. speed < 15 km/h   (stop-and-go or jam)
2. speedTrend = decelerating for ≥ 3 consecutive ticks
3. distance < 8 m    (bumper-to-bumper — expected in congestion)
```

Or when:

```
4. speed < 5 km/h AND accelMagnitude < 2.0    (stopped at signal)
5. speed history shows repeated 0→5→0 cycles  (traffic light pattern)
```

---

## Alert Levels

| State | Trigger | Message | Severity | Voice |
|-------|---------|---------|----------|-------|
| IDEAL | distance ≥ ideal | — | (none) | — |
| ADVISORY | warn < distance < ideal | "Maintain following distance" | info | Once per 90 s |
| WARNING | min < distance ≤ warn | "Too close — ease off" | warning | Once per 30 s |
| CRITICAL | distance ≤ min AND speed > 30 | "Danger — back off immediately" | danger | Immediate |
| PERSONAL_DRIFT | distance < 80% baseline | "Closer than your usual gap" | info | Once per 2 min |

**Alert cooldowns** prevent repeat firing:
- ADVISORY: minimum 90 s between alerts
- WARNING: minimum 30 s between alerts  
- CRITICAL: no cooldown — fires every 5 s while condition persists
- PERSONAL_DRIFT: minimum 120 s between alerts

---

## Data Flow

```
Sensor (front camera TTC / WebSerial ultrasonic)
        │
        ▼
sensor-monitor.js  ──→  frontDistanceM value
        │
        ▼
highway-algorithm.js
  UpdatePosition() includes frontDistanceM
        │
        ▼
DriveHub.cs — UpdatePosition(lat, lng, speed, accel, db, frontDistanceM)
        │
        ▼
FollowingDistanceAlgorithm.Evaluate(sessionId, speed, frontDistanceM, context)
        │
        ├── Returns: FollowingDistanceAlert?
        │
        ▼
DriveSessionData.FollowingDistanceSamples.Add(sample)
        │
        ▼
On EndSession() → FollowingDistanceHistoryService.SaveRideSummary()
        │
        ▼
DB: FollowingDistanceHistory (last 10 per band per user)
```

---

## New Files

| File | Purpose |
|------|---------|
| `Models/FollowingDistanceHistory.cs` | EF Core entity — 10-ride history per user per speed band |
| `Models/AlertSettings.cs` (updated) | New config fields for distance thresholds per band |
| `Services/FollowingDistanceAlgorithm.cs` | Core evaluation engine |
| `Services/FollowingDistanceHistoryService.cs` | Loads/saves 10-ride rolling average |
| `Controllers/Api/FollowingDistanceController.cs` | REST: GET history, GET baseline, DELETE ride |
| `wwwroot/js/distance-monitor.js` | Front-end sensor reader (TTC + WebSerial) |
| `wwwroot/js/highway-algorithm.js` (patch) | Wire frontDistanceM into UpdatePosition call |
