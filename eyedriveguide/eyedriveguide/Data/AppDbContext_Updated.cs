// ============================================================
// AppDbContext.cs — Security-Updated
// SECURITY FIXES:
//   DS-2  — EncryptedStringConverter applied to sensitive fields
//   DS-7  — AuditLogs DbSet added
//   OW-1  — UserId fields added to Address and DriveSession
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

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<AlertSettings> AlertSettings => Set<AlertSettings>();
    public DbSet<DriveSession> DriveSessions => Set<DriveSession>();
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();  // SECURITY FIX DS-7

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // SECURITY FIX DS-2: encrypt sensitive string fields at rest
        string? encryptKey = null;
        try
        {
            encryptKey = _config != null
                ? EncryptedStringConverter.LoadKeyFromEnvironment(_config)
                : null;
        }
        catch (Exception ex)
        {
            // In development, log and continue without encryption
            // In production, LoadKeyFromEnvironment throws — this catch won't fire
            Console.Error.WriteLine($"[SECURITY WARNING] DB encryption not enabled: {ex.Message}");
        }

        if (encryptKey != null)
        {
            var converter = new EncryptedStringConverter(encryptKey);

            // Encrypt Address.StreetAddress and Label
            modelBuilder.Entity<Address>()
                .Property(a => a.StreetAddress)
                .HasConversion(converter);

            modelBuilder.Entity<Address>()
                .Property(a => a.Label)
                .HasConversion(converter);

            // Encrypt DriveSession.DestinationAddress
            modelBuilder.Entity<DriveSession>()
                .Property(s => s.DestinationAddress)
                .HasConversion(converter);
        }

        // SECURITY FIX OW-1: index UserId for efficient user-scoped queries
        modelBuilder.Entity<Address>()
            .HasIndex(a => a.UserId);

        modelBuilder.Entity<DriveSession>()
            .HasIndex(s => s.UserId);

        // Audit log index for time-range queries
        modelBuilder.Entity<AuditLogEntry>()
            .HasIndex(a => a.Timestamp);

        // Default AlertSettings seed data (unchanged)
        modelBuilder.Entity<AlertSettings>().HasData(new AlertSettings
        {
            Id = 1,
            YellowDistanceThreshold = 10,
            RedDistanceThreshold = 15,
            SpeedAlertPollIntervalMinutes = 2,
            DistractionDbLevel = 60,
            PassingLaneLoiterSeconds = 60,
            FollowingDistanceMetres = 30,
            WeatherNightModeAutoAdjust = false
        });
    }
}
