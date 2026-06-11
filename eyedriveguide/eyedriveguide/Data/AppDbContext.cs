using EyeDriveGuide.Models;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
        public DbSet<Address> Addresses => Set<Address>();
        public DbSet<AlertSettings> AlertSettings => Set<AlertSettings>();
        public DbSet<DriveSession> DriveSessions => Set<DriveSession>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

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
}
