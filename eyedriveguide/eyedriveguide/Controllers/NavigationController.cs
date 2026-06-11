using EyeDriveGuide.Data;
using EyeDriveGuide.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Controllers
{
    public class NavigationController : Controller
    {
        private readonly AppDbContext _db;

        public NavigationController(AppDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            var vm = new NavigationViewModel
            {
                Addresses = await _db.Addresses.OrderBy(a => a.Type).ThenBy(a => a.Label).ToListAsync(),
                Settings = await _db.AlertSettings.FirstOrDefaultAsync() ?? new()
            };
            return View(vm);
        }
    }
}
