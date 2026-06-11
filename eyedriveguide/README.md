# eyedriveguide

Highway driving safety guidance app built with ASP.NET Core MVC.

## Features

- **Real-time drive session** via SignalR hub — GPS position, speed, microphone dB level, camera optical-flow, and accelerometer fed from browser APIs
- **Highway algorithm** — merge guidance, lane discipline, exit staging, and construction-zone alerts
- **Night / weather mode** — NOAA solar elevation + Open-Meteo API; tightens speed thresholds automatically at night or in bad weather
- **Destinations** — saved address book with lat/lng coordinates; route loaded from OpenRouteService on drive start
- **Leaflet.js map** — live position marker, route polyline, and per-segment speed-limit display
- **Post-drive summary** — speed consistency, alert counts, distraction events
- **Configuration page** — user profile, alert thresholds, address management

## Stack

| Layer | Technology |
|-------|-----------|
| Framework | ASP.NET Core MVC (net8.0) |
| Real-time | SignalR |
| Database | SQLite + EF Core (`EnsureCreated`) |
| Map | Leaflet.js |
| Front-end | Vanilla JS modules + Bootstrap 5 |
| Routing API | OpenRouteService |
| Weather API | Open-Meteo / NOAA |

## Getting started

```bash
# Requires .NET 8 SDK
cd eyedriveguide
dotnet restore
dotnet run --project eyedriveguide/eyedriveguide.csproj --urls http://localhost:5000
```

Open http://localhost:5000 in a browser. Grant location, microphone, and camera permissions when prompted.

## Project structure

```
eyedriveguide/
├── eyedriveguide.sln
└── eyedriveguide/
    ├── Controllers/          # MVC + API controllers
    ├── Hubs/DriveHub.cs      # SignalR hub — drive loop
    ├── Models/               # EF Core entities
    ├── Services/             # MergeAlgorithm, LaneDisciplineAlgorithm, ExitAlgorithm, RouteService
    ├── Views/
    │   ├── Home/             # Landing page
    │   ├── Configuration/    # Settings, addresses, alert thresholds
    │   └── Navigation/       # Live drive page
    └── wwwroot/js/
        ├── highway-algorithm.js   # Drive loop orchestration
        ├── sensor-monitor.js      # GPS / mic / camera / accel
        ├── map-controller.js      # Leaflet map
        ├── alert-system.js        # TTS + visual alerts
        └── address-manager.js     # Address CRUD
```
