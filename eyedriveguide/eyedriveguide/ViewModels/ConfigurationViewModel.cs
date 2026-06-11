using EyeDriveGuide.Models;

namespace EyeDriveGuide.ViewModels
{
    public class ConfigurationViewModel
    {
        public UserProfile Profile { get; set; } = new();
        public AlertSettings Settings { get; set; } = new();
        public List<Address> Addresses { get; set; } = new();
        public bool SaveSuccess { get; set; }
    }
}
