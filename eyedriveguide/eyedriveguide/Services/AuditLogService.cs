// ============================================================
// AuditLogService.cs — SECURITY FIX DS-7 / OW-7
// Lightweight audit trail for all security-relevant mutations.
// Writes to security-audit.log (structured) and the DB.
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Models;

namespace EyeDriveGuide.Services;

public class AuditLogService(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditLogService> logger)
{
    public async Task LogAsync(
        string action,
        string entityType,
        string? entityId,
        string? ipAddress,
        string? userId,
        string? detail = null)
    {
        // Structured log for SIEM ingestion
        logger.LogInformation(
            "[AUDIT] Action={Action} Entity={EntityType} EntityId={EntityId} " +
            "UserId={UserId} IP={IP} Detail={Detail}",
            action, entityType, entityId, userId, ipAddress, detail);

        // Persist to DB for in-app audit review
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.AuditLogs.Add(new AuditLogEntry
            {
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                UserId = userId,
                IpAddress = ipAddress,
                Detail = detail,
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let audit failure break the primary operation
            logger.LogWarning(ex, "Failed to persist audit log entry");
        }
    }
}
