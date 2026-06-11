// ============================================================
// AuditLogEntry.cs — SECURITY FIX DS-7 / OW-7
// EF Core entity for the audit log table.
// ============================================================
namespace EyeDriveGuide.Models;

public class AuditLogEntry
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;       // e.g. ADDRESS_CREATE
    public string EntityType { get; set; } = string.Empty;   // e.g. Address
    public string? EntityId { get; set; }
    public string? UserId { get; set; }
    public string? IpAddress { get; set; }
    public string? Detail { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
