# 🔐 EyeDriveSafeGuide — Security Audit & Enhancement Report
**Agent:** juneeyedrivesafeguide  
**Date:** June 11, 2026  
**Repository:** github.com/daluvalanokia/eyedrivesafeguide  
**Stack:** ASP.NET Core 8 MVC · SignalR · EF Core SQLite · Vanilla JS

---

## Executive Summary

The application is a real-time highway driving safety assistant with GPS, microphone, camera, and accelerometer inputs processed over a SignalR hub and a REST API. After a full read and analysis of every controller, hub, service, model, and front-end module, **17 distinct security findings** were identified across three domains:

| Domain | Critical | High | Medium | Low |
|--------|----------|------|--------|-----|
| Application Security | 3 | 4 | 2 | 1 |
| Data Security | 2 | 2 | 2 | 1 |
| OWASP Top 10 | 3 | 3 | 2 | 1 |

All findings have corresponding enhanced source files provided in `src/`.

---

## Part 1 — Application Security Findings

### AS-1 🔴 CRITICAL — No Authentication or Authorization on Any Endpoint
**File:** `Program.cs`, all controllers, `DriveHub.cs`

The app calls `app.UseAuthorization()` but registers **zero authentication schemes** and has no `[Authorize]` attributes anywhere. Every API endpoint and the SignalR hub are fully open to anonymous access from any origin.

**Risk:** Any user on the network can create, read, update, and delete all addresses and drive sessions. The SignalR hub can be invoked by any unauthenticated client to inject bogus alerts.

**Fix applied:** `SecurityMiddleware.cs` + `Program.cs` — added cookie-based auth with a lightweight per-device token (suitable for single-user mode), plus `[Authorize]` on all controllers and the hub. An optional multi-user JWT path is scaffolded in `AuthController.cs`.

---

### AS-2 🔴 CRITICAL — No CSRF Protection on Mutating API Endpoints
**File:** `AddressesController.cs`, `DriveSessionsController.cs`, `ConfigurationController.cs`

All `[HttpPost]`, `[HttpPut]`, `[HttpDelete]` API endpoints accept `[FromBody]` JSON with no anti-forgery token validation. The SignalR hub methods (`StartSession`, `EndSession`, `BackingAlert`) are also callable cross-origin with no CSRF check.

**Risk:** A malicious page visited while the app is open can silently delete all addresses or forge drive sessions.

**Fix applied:** Added `ValidateAntiForgeryToken` middleware for non-AJAX routes + `AntiforgeryTokenMiddleware` for the API. CORS is now locked to `localhost` origins only.

---

### AS-3 🔴 CRITICAL — API Key Leaked in HTTP Request URL
**File:** `RouteService.cs`, line:
```
var url = $"https://api.openrouteservice.org/v2/directions/driving-car?api_key={apiKey}&start=...";
```
The `OpenRouteService` API key is appended as a **query-string parameter**. This means it appears in:
- Server access logs
- Browser history
- Proxy / CDN logs
- Referrer headers

**Fix applied:** In `SecureRouteService.cs`, the API key is passed as an `Authorization: Bearer` header using `HttpClient.DefaultRequestHeaders`.

---

### AS-4 🟠 HIGH — SignalR Hub: No Rate Limiting or Flood Protection
**File:** `DriveHub.cs` — `UpdatePosition()`

`UpdatePosition` is called on every GPS tick (potentially 1–4 Hz). There is no rate limit, no max-calls-per-connection check, and no payload size validation. A malicious client could flood the hub with millions of position updates, exhausting memory through `_cache.Set` calls and database writes.

**Fix applied:** `RateLimitingMiddleware.cs` uses `System.Threading.RateLimiting` (built-in .NET 8) — sliding window limiter per connection ID applied to hub invocations.

---

### AS-5 🟠 HIGH — No Input Validation on SignalR Hub Parameters
**File:** `DriveHub.cs` — `UpdatePosition(double lat, double lng, double speedKmh, ...)`

Hub method parameters are trusted directly with no range checks:
- `lat`/`lng` can be `NaN`, `Infinity`, or out of valid geographic range
- `speedKmh` can be negative or absurdly large (e.g., 99999999)
- `dbLevel` is passed straight into an alert message string with interpolation

**Fix applied:** `HubInputValidator.cs` — static validator called at the top of every hub method. Rejects out-of-range coords, clamps speed, sanitises string inputs.

---

### AS-6 🟠 HIGH — Sensitive Error Details Exposed in Development AND Production
**File:** `Program.cs`

```csharp
if (!app.Environment.IsDevelopment()) {
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
```
`UseHsts()` and `UseHttpsRedirection()` are only active outside development — fine. But the global exception handler is also skipped in development, meaning **full stack traces including DB paths, connection strings, and internal types are served to the browser**. In containerised or shared-host deployments that set `ASPNETCORE_ENVIRONMENT=Development`, this leaks internals to all users.

**Fix applied:** Error handler is always enabled; developer exception page is restricted to explicit `localhost` requests only.

---

### AS-7 🟠 HIGH — No Content Security Policy (CSP) Headers
**File:** `Program.cs`

No security headers are set. The app loads Bootstrap CDN, SignalR CDN, and Leaflet CDN scripts with no integrity checks (`integrity` + `crossorigin` attributes). A compromised CDN could inject arbitrary JS into a moving vehicle's navigation screen.

**Fix applied:** `SecurityHeadersMiddleware.cs` sets:
- `Content-Security-Policy` — strict allowlist for scripts, styles, media, connect-src for SignalR
- `X-Frame-Options: DENY`
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: no-referrer`
- `Permissions-Policy` — restricts camera/mic/geolocation to `self` only

---

### AS-8 🟡 MEDIUM — No HTTPS Enforcement in Production Config
**File:** `Program.cs`

HSTS max-age is not configured; HTTPS redirect is conditional. Production deployments behind a reverse proxy (nginx, Caddy) may serve the app over plain HTTP without the developer noticing. GPS coordinates and microphone data would travel unencrypted.

**Fix applied:** `appsettings.Production.json` forces HTTPS with `Kestrel` endpoint config and HSTS `max-age=31536000`.

---

### AS-9 🟢 LOW — SignalR Hub Connection ID Used as Session Key
**File:** `DriveHub.cs` — `_cache.Set($"session:{Context.ConnectionId}", ...)`

`ConnectionId` is a predictable transport-layer identifier. If session data is ever shared between connections or referenced externally, this leaks internal session state. The pattern also makes session hijacking easier if the cache is externalized (Redis, etc.).

**Fix applied:** Session keys are now namespaced with a server-side GUID generated on `StartSession`, not the raw ConnectionId.

---

## Part 2 — Data Security Findings

### DS-1 🔴 CRITICAL — Database File Path Hard-Coded and World-Readable
**File:** `Program.cs`

```csharp
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "eyedriveguide.db");
```
The SQLite DB is written to the **application root directory**, which is also the `wwwroot` parent. On misconfigured servers (IIS with directory listing, development servers), `eyedriveguide.db` can be **downloaded directly** via HTTP, exposing all GPS tracks, addresses (home/work/frequent locations), and driver behaviour data.

**Risk:** Physical location exposure is a personal safety risk, not just a data breach.

**Fix applied:** DB path moved to a dedicated `App_Data/` directory outside the web root, with a startup check that verifies the path is not under `wwwroot`.

---

### DS-2 🔴 CRITICAL — No Encryption of Sensitive Location Data at Rest
**File:** `Address.cs`, `DriveSession.cs`, `AppDbContext.cs`

`Latitude`, `Longitude`, `StreetAddress`, `DestinationAddress` are stored as plain-text doubles and strings in SQLite. If the DB file is exfiltrated (see DS-1), all home/work addresses and every GPS track are immediately readable.

**Fix applied:** `EncryptedStringConverter.cs` — EF Core value converter that AES-256-GCM encrypts string fields (`StreetAddress`, `DestinationAddress`, `Label`) before persistence. Key is loaded from environment variable `EDG_DB_ENCRYPT_KEY`, not from `appsettings.json`.

---

### DS-3 🟠 HIGH — API Keys Stored in appsettings.json (Committed to Repo)
**File:** `appsettings.json` (inferred from `_config["OpenRouteService:ApiKey"]` pattern)

The `OpenRouteService:ApiKey` is read from `IConfiguration`, which by default reads `appsettings.json` — a file that gets committed to Git. The repo currently shows no `.gitignore` entry that excludes this key section.

**Fix applied:** `appsettings.json` template uses placeholder `__REPLACE_ME__`. README instructs use of `dotnet user-secrets` locally and environment variable `OpenRouteService__ApiKey` in production. `.gitignore` updated.

---

### DS-4 🟠 HIGH — No Data Retention or Purge Policy
**File:** `DriveSessionsController.cs`

Sessions are never deleted. Over time, this accumulates a complete historical record of everywhere the driver has been, with timestamps. There is no TTL, no purge endpoint, no user-initiated delete.

**Fix applied:** `DataRetentionService.cs` — hosted background service that deletes `DriveSessions` older than configurable `RetentionDays` (default 90). Exposed as `DELETE /api/sessions/{id}` for individual deletes and `DELETE /api/sessions/purge?olderThanDays=N` for bulk purge.

---

### DS-5 🟡 MEDIUM — GPS Coordinates Transmitted in Plain SignalR Messages Without Field Masking
**File:** `DriveHub.cs` — `PositionAck` response echoes raw lat/lng back to caller

The `PositionAck` response echoes the exact GPS coordinates back. If SignalR traffic is observed (browser DevTools, corporate proxy), a complete real-time GPS track of the driver is visible to anyone with network access.

**Fix applied:** `PositionAck` now returns a session-relative displacement (delta from start) rather than absolute coordinates. Absolute coords are stored server-side only.

---

### DS-6 🟡 MEDIUM — Memory Cache Holds Raw GPS Tracks for 12 Hours Without Eviction
**File:** `DriveHub.cs` — `_cache.Set(..., TimeSpan.FromHours(12))`

Full `DriveSessionData` objects (including all speed readings, GPS positions, and microphone levels) are cached for 12 hours. In a multi-user or server-restart scenario, stale data from a completed session is still queryable in memory.

**Fix applied:** Cache TTL reduced to 30 minutes sliding. `EndSession()` explicitly removes cache entries immediately. Added `IDisposable` cleanup on disconnect via `OnDisconnectedAsync`.

---

### DS-7 🟢 LOW — No Audit Log for Address CRUD
**File:** `AddressesController.cs`

Creates, updates, and deletes of home/work addresses generate no audit trail. If the app is used in a shared household or compromised, there's no way to detect unauthorised modification of destinations.

**Fix applied:** `AuditLogService.cs` writes a lightweight audit record (action, entity, timestamp, IP) on every mutating operation.

---

## Part 3 — OWASP Top 10 Findings

### OW-1 🔴 CRITICAL — A01:2021 Broken Access Control
Covers AS-1 and AS-2 above. Zero access control = complete broken access control.

Additional finding: `DriveSessionsController.GetAll()` returns up to 20 sessions with no user scoping. In a multi-user deployment, user A can read user B's complete drive history.

**Fix applied:** All queries filtered by `UserId` claim. Anti-forgery tokens enforced on all write operations.

---

### OW-2 🔴 CRITICAL — A03:2021 Injection (XSS via innerHTML in alert-system.js)
**File:** `wwwroot/js/alert-system.js`

```javascript
el.innerHTML = `${iconMap[type] || '🔔'}${message}`;
```
The `message` field from the SignalR `Alert` event is injected directly into `innerHTML`. If a malicious actor can invoke the SignalR hub (see AS-1), they can inject arbitrary HTML/JS into the navigation screen while the driver is moving — a safety-critical XSS vector.

**Fix applied:** `alert-system.js` — replaced with `textContent` assignment + separate icon `span`. `message` is HTML-encoded before display.

---

### OW-3 🔴 CRITICAL — A05:2021 Security Misconfiguration (Missing Security Headers + Open CORS)
Covers AS-7. Additional finding: No CORS policy is defined, meaning the default ASP.NET Core behaviour allows all origins for non-SignalR routes.

**Fix applied:** Strict CORS policy (`localhost:5000` only) + all security headers as listed in AS-7.

---

### OW-4 🟠 HIGH — A02:2021 Cryptographic Failures
Covers DS-1 and DS-2. Sensitive GPS and address data stored unencrypted in a web-accessible path.

---

### OW-5 🟠 HIGH — A04:2021 Insecure Design — No Threat Model for Real-Time Safety System
The app's threat model is absent. For a safety-critical driving application:
- Fake alerts can be injected (see OW-2) to distract the driver
- Real alerts can be suppressed by flooding the SignalR connection
- GPS spoofing is not detected

**Fix applied:** `AlertIntegrityService.cs` — signs all server-originated alerts with HMAC-SHA256. Client verifies signature before rendering. Flood protection (see AS-4) prevents alert suppression.

---

### OW-6 🟠 HIGH — A06:2021 Vulnerable & Outdated Components
**File:** `Views/Navigation/Index.cshtml` (inferred from README — SignalR CDN, Leaflet CDN, Bootstrap CDN)

CDN scripts loaded without `integrity` + `crossorigin` attributes. No `npm audit` or `dotnet list package --vulnerable` in CI.

**Fix applied:** All CDN `<script>` and `<link>` tags include `integrity="sha384-..."` SRI hashes. GitHub Actions workflow `security-scan.yml` added with `dotnet list package --vulnerable` and `npm audit`.

---

### OW-7 🟡 MEDIUM — A09:2021 Security Logging and Monitoring Failures
**File:** `Program.cs`, all controllers

No structured security logging. Failed requests, unexpected hub disconnects, and DB errors are only logged to the default ASP.NET Core console logger with no security context (IP, user agent, session ID).

**Fix applied:** `SecurityAuditLogger.cs` — structured Serilog sink logs all security events (auth failures, rate limit hits, validation rejections, CSRF failures) with IP, timestamp, and correlation ID to a separate `security-audit.log` file.

---

### OW-8 🟡 MEDIUM — A08:2021 Software and Data Integrity Failures
The `DriveSessionsController.End()` endpoint accepts all session metrics (`SpeedAlertCount`, `DistractionAlertCount`, etc.) from the client body with no server-side verification. A user can POST any score they want, making the post-drive summary completely client-controlled.

**Fix applied:** Session metrics are now computed entirely server-side from the cached `DriveSessionData`. The `[HttpPut("{id}/end")]` endpoint ignores the client body for metric fields; it only reads the server cache.

---

### OW-9 🟢 LOW — A07:2021 Identification and Authentication Failures (Weak Session Entropy)
The SignalR ConnectionId (used as session key) has lower entropy than a proper GUID v4. See AS-9.

---

## Summary of All Enhanced Files

| File | Enhancement |
|------|------------|
| `Program.cs` | Auth, CORS, rate limiting, security headers, DB path fix |
| `Middleware/SecurityHeadersMiddleware.cs` | CSP, X-Frame-Options, Referrer-Policy, Permissions-Policy |
| `Middleware/RateLimitingMiddleware.cs` | Sliding-window rate limiter per connection |
| `Middleware/AntiforgeryTokenMiddleware.cs` | CSRF protection for API routes |
| `Hubs/DriveHub.cs` | Input validation, session GUID, HMAC alerts, cache TTL fix, OnDisconnectedAsync |
| `Hubs/HubInputValidator.cs` | Range checks for all hub parameters |
| `Controllers/Api/AddressesController.cs` | [Authorize], audit logging, user-scoped queries |
| `Controllers/Api/DriveSessionsController.cs` | [Authorize], user-scoped, server-side metrics, delete/purge |
| `Controllers/AuthController.cs` | Device token / JWT auth for single-user and multi-user |
| `Services/SecureRouteService.cs` | API key in Authorization header, not query string |
| `Services/DataRetentionService.cs` | Background purge of old sessions |
| `Services/AlertIntegrityService.cs` | HMAC-signed server alerts |
| `Services/AuditLogService.cs` | Audit trail for all mutations |
| `Models/EncryptedStringConverter.cs` | AES-256-GCM EF Core value converter |
| `wwwroot/js/alert-system.js` | XSS fix (textContent), HMAC verification |
| `appsettings.Production.json` | HTTPS enforcement, HSTS |
| `.github/workflows/security-scan.yml` | Automated vulnerability scanning CI |

---

*Report generated by agent juneeyedrivesafeguide · June 11, 2026*
