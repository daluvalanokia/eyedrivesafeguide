using EyeDriveGuide.Models;

namespace EyeDriveGuide.ViewModels
{
    public class NavigationViewModel
    {
        public List<Address> Addresses { get; set; } = new();
        public AlertSettings Settings { get; set; } = new();
    }
}
