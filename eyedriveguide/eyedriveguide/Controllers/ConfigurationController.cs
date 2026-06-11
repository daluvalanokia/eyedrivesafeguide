using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using EyeDriveGuide.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Controllers
{
    public class ConfigurationController : Controller
    {
        private readonly AppDbContext _db;

        public ConfigurationController(AppDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            var vm = new ConfigurationViewModel
            {
                Profile = await _db.UserProfiles.FirstOrDefaultAsync() ?? new UserProfile(),
                Settings = await _db.AlertSettings.FirstOrDefaultAsync() ?? new AlertSettings(),
                Addresses = await _db.Addresses.OrderBy(a => a.Type).ThenBy(a => a.Label).ToListAsync()
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveProfile(UserProfile profile)
        {
            var existing = await _db.UserProfiles.FirstOrDefaultAsync();
            if (existing == null)
            {
                profile.CreatedAt = DateTime.UtcNow;
                profile.UpdatedAt = DateTime.UtcNow;
                _db.UserProfiles.Add(profile);
            }
            else
            {
                existing.FullName = profile.FullName;
                existing.PhoneNumber = profile.PhoneNumber;
                existing.Email = profile.Email;
                existing.DeviceMake = profile.DeviceMake;
                existing.DeviceModel = profile.DeviceModel;
                existing.Imei = profile.Imei;
                existing.OsBuild = profile.OsBuild;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = "Profile saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSettings(AlertSettings settings)
        {
            var existing = await _db.AlertSettings.FirstOrDefaultAsync();
            if (existing == null)
            {
                _db.AlertSettings.Add(settings);
            }
            else
            {
                existing.YellowDistanceThreshold = settings.YellowDistanceThreshold;
                existing.RedDistanceThreshold = settings.RedDistanceThreshold;
                existing.SpeedAlertPollIntervalMinutes = settings.SpeedAlertPollIntervalMinutes;
                existing.DistractionDbLevel = settings.DistractionDbLevel;
                existing.PassingLaneLoiterSeconds = settings.PassingLaneLoiterSeconds;
                existing.FollowingDistanceMetres = settings.FollowingDistanceMetres;
                existing.WeatherNightModeAutoAdjust = settings.WeatherNightModeAutoAdjust;
            }
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = "Alert settings saved.";
            return RedirectToAction(nameof(Index));
        }
    }
}
