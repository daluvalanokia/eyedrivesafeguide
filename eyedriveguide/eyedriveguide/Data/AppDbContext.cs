// ============================================================
// AppDbContext.cs — Security-Updated (merged, single definition)
// REPLACES both AppDbContext.cs and AppDbContext_Updated.cs
// SECURITY FIXES:
//   DS-2 — EncryptedStringConverter on sensitive fields
//   DS-7 — AuditLogs DbSet
//   OW-1 — UserId indexes on Address and DriveSession
// ============================================================
using EyeDriveGuide.Models;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Data;

public class AppDbContext : DbContext
{
    private readonly IConfiguration? _config;

    public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration? config = null)
        : base(options)
    {
        _config = config;
    }

    public DbSet<UserProfile>          UserProfiles  => Set<UserProfile>();
    public DbSet<Address>              Addresses     => Set<Address>();
    public DbSet<AlertSettings>        AlertSettings => Set<AlertSettings>();
    public DbSet<DriveSession>         DriveSessions => Set<DriveSession>();
    public DbSet<AuditLogEntry>        AuditLogs     => Set<AuditLogEntry>();
    public DbSet<FollowingDistanceHistory> FollowingDistanceHistory => Set<FollowingDistanceHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── SECURITY FIX DS-2: encrypt sensitive fields at rest ──
        string? encryptKey = null;
        try
        {
            encryptKey = _config != null
                ? EncryptedStringConverter.LoadKeyFromEnvironment(_config)
                : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SECURITY WARNING] DB encryption not enabled: {ex.Message}");
        }

        if (encryptKey != null)
        {
            var converter = new EncryptedStringConverter(encryptKey);
            modelBuilder.Entity<Address>().Property(a => a.StreetAddress).HasConversion(converter);
            modelBuilder.Entity<Address>().Property(a => a.Label).HasConversion(converter);
            modelBuilder.Entity<DriveSession>().Property(s => s.DestinationAddress).HasConversion(converter);
        }

        // ── SECURITY FIX OW-1: index UserId for user-scoped queries ──
        modelBuilder.Entity<Address>().HasIndex(a => a.UserId);
        modelBuilder.Entity<DriveSession>().HasIndex(s => s.UserId);
        modelBuilder.Entity<AuditLogEntry>().HasIndex(a => a.Timestamp);

        // ── FollowingDistanceHistory: index per user+band ──
        modelBuilder.Entity<FollowingDistanceHistory>()
            .HasIndex(h => new { h.UserId, h.Band });

        // ── Seed default AlertSettings ──
        modelBuilder.Entity<AlertSettings>().HasData(new AlertSettings
        {
            Id = 1,
            YellowDistanceThreshold      = 10,
            RedDistanceThreshold         = 15,
            SpeedAlertPollIntervalMinutes = 2,
            DistractionDbLevel           = 60,
            PassingLaneLoiterSeconds     = 60,
            FollowingDistanceMetres      = 30,
            WeatherNightModeAutoAdjust   = false
        });
    }
}
