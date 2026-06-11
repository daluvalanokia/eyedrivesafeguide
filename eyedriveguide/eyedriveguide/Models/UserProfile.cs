// ============================================================
// UserProfile.cs — Extended
// FIX: adds SimulationEnabled + SimulationDefaultSpeedKmh
//      required by SimulationController (CS1061).
// ============================================================
using System.ComponentModel.DataAnnotations;

namespace EyeDriveGuide.Models;

public class UserProfile
{
    public int Id { get; set; }

    [Display(Name = "Full Name")]
    public string? FullName { get; set; }

    [Display(Name = "Phone Number")]
    public string? PhoneNumber { get; set; }

    [EmailAddress]
    public string? Email { get; set; }

    [Display(Name = "Device Make")]
    public string? DeviceMake { get; set; }

    [Display(Name = "Device Model")]
    public string? DeviceModel { get; set; }

    [Display(Name = "IMEI")]
    public string? Imei { get; set; }

    [Display(Name = "OS Build")]
    public string? OsBuild { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Simulation preferences (added for SimulationController) ──
    [Display(Name = "Simulation Mode Enabled")]
    public bool SimulationEnabled { get; set; } = false;

    [Display(Name = "Simulation Default Speed (km/h)")]
    [Range(0, 120)]
    public int SimulationDefaultSpeedKmh { get; set; } = 50;
}
