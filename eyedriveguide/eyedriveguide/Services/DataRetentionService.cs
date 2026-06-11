// ============================================================
// DataRetentionService.cs — SECURITY FIX DS-4
// Background service that purges old DriveSession records.
// Configurable via appsettings: DataRetention:SessionRetentionDays
// Default: 90 days. Runs daily at 02:00 UTC.
// ============================================================
using EyeDriveGuide.Data;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Services;

public class DataRetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<DataRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DataRetentionService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRetentionAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "DataRetentionService encountered an error");
            }

            // Schedule next run: sleep until 02:00 UTC tomorrow
            var now = DateTime.UtcNow;
            var nextRun = now.Date.AddDays(1).AddHours(2);
            var delay = nextRun - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromHours(24);

            logger.LogInformation("Next retention run scheduled at {NextRun:u}", nextRun);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RunRetentionAsync(CancellationToken ct)
    {
        var retentionDays = config.GetValue("DataRetention:SessionRetentionDays", 90);
        if (retentionDays < 1)
        {
            logger.LogWarning("DataRetention:SessionRetentionDays is < 1 — skipping purge");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Delete in batches to avoid locking the SQLite DB for too long
        int totalDeleted = 0;
        int batchSize = 100;

        while (true)
        {
            var batch = await db.DriveSessions
                .Where(s => s.StartedAt < cutoff)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            db.DriveSessions.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            totalDeleted += batch.Count;

            if (batch.Count < batchSize) break;

            await Task.Delay(100, ct); // Brief pause between batches
        }

        if (totalDeleted > 0)
        {
            logger.LogInformation(
                "DataRetentionService: purged {Count} sessions older than {Days} days",
                totalDeleted, retentionDays);
        }
    }
}
