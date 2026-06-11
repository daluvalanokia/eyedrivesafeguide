using System.ComponentModel.DataAnnotations;

namespace EyeDriveGuide.Models
{
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
    }
}
