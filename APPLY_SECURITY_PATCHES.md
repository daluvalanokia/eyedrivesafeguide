# 🛡️ How to Apply Security Patches — juneeyedrivesafeguide

## Step 1 — Set Required Environment Variables

```bash
# Generate secrets (run once, store securely)
export EDG_DEVICE_TOKEN=$(openssl rand -hex 32)
export EDG_DB_ENCRYPT_KEY=$(openssl rand -base64 32)
export EDG_ALERT_SIGNING_KEY=$(openssl rand -hex 32)
export OpenRouteService__ApiKey=your_ors_api_key_here

# For development (dotnet user-secrets)
cd eyedriveguide/eyedriveguide
dotnet user-secrets set "EDG_DEVICE_TOKEN" "$(openssl rand -hex 32)"
dotnet user-secrets set "EDG_DB_ENCRYPT_KEY" "$(openssl rand -base64 32)"
dotnet user-secrets set "EDG_ALERT_SIGNING_KEY" "$(openssl rand -hex 32)"
dotnet user-secrets set "OpenRouteService__ApiKey" "your_key_here"
```

## Step 2 — Replace / Add Files

| Source file (this repo) | Target location in eyedrivesafeguide |
|------------------------|--------------------------------------|
| `src/Program.cs` | `eyedriveguide/eyedriveguide/Program.cs` |
| `src/Middleware/SecurityHeadersMiddleware.cs` | `eyedriveguide/eyedriveguide/Middleware/` |
| `src/Middleware/AntiforgeryTokenMiddleware.cs` | `eyedriveguide/eyedriveguide/Middleware/` |
| `src/Hubs/HubInputValidator.cs` | `eyedriveguide/eyedriveguide/Hubs/` |
| `src/Hubs/DriveHub_SecurityPatch.cs` | Merge into existing `DriveHub.cs` |
| `src/Controllers/Api/AddressesController.cs` | Replace existing |
| `src/Controllers/Api/DriveSessionsController.cs` | Replace existing |
| `src/Controllers/AuthController.cs` | New file |
| `src/Services/SecureRouteService.cs` | Replace `RouteService.cs` |
| `src/Services/AlertIntegrityService.cs` | New file |
| `src/Services/DataRetentionService.cs` | New file |
| `src/Services/AuditLogService.cs` | New file |
| `src/Models/EncryptedStringConverter.cs` | New file |
| `src/Models/AuditLogEntry.cs` | New file |
| `src/Models/Address_UserScoped.cs` | Merge into `Address.cs` (add UserId) |
| `src/Models/DriveSession_UserScoped.cs` | Merge into `DriveSession.cs` (add UserId) |
| `src/AppDbContext_Updated.cs` | Replace `Data/AppDbContext.cs` |
| `src/wwwroot/js/alert-system.js` | Replace existing |
| `src/appsettings.Production.json` | New file |
| `src/.github/workflows/security-scan.yml` | New file |

## Step 3 — Add EF Core Migration

```bash
cd eyedriveguide/eyedriveguide
dotnet ef migrations add SecurityHardening --project . --startup-project .
dotnet ef database update
```

## Step 4 — Add Login Page

Add `Views/Auth/Login.cshtml` with a form that POSTs to `/auth/login` with the `deviceToken` field.

## Step 5 — Update CDN Script Tags with SRI Hashes

In all `.cshtml` files that load external scripts, add `integrity` and `crossorigin` attributes:

```html
<!-- Example Bootstrap -->
<link rel="stylesheet"
  href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css"
  integrity="sha384-QWTKZyjpPEjISv5WaRU9OFeRpok6YctnYmDr5pNlyT2bRjXh0JMhjY6hW+ALEwIH"
  crossorigin="anonymous">

<!-- Example SignalR -->
<script
  src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js"
  integrity="sha384-..."
  crossorigin="anonymous"></script>
```

Use https://www.srihash.org to generate hashes for your exact CDN versions.

## Step 6 — Verify

```bash
# Run the app and check:
# 1. Unauthenticated requests to /api/addresses return 401
# 2. Browser DevTools → Network: check response headers for CSP, X-Frame-Options
# 3. Database file is at App_Data/eyedriveguide.db (not content root)
# 4. No API key visible in Network tab when loading a route
dotnet run --project eyedriveguide/eyedriveguide.csproj --environment Production
```

## Priority Order (if applying incrementally)

1. 🔴 **AS-3** — Fix API key in URL (10 min, high impact)
2. 🔴 **DS-1** — Move DB outside web root (5 min)
3. 🔴 **AS-1** — Add authentication (1–2 hours)
4. 🔴 **OW-2** — Fix XSS in alert-system.js (15 min, safety-critical)
5. 🟠 **AS-7** — Add security headers (30 min)
6. 🟠 **AS-4** — Add rate limiting (30 min)
7. 🟠 **AS-5** — Add hub input validation (1 hour)
8. 🔴 **DS-2** — Encrypt address data at rest (1–2 hours)
