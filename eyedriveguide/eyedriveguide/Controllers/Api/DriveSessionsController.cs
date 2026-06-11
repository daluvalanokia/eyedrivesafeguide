using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Controllers.Api
{
    [ApiController]
    [Route("api/sessions")]
    public class DriveSessionsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public DriveSessionsController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var sessions = await _db.DriveSessions
                .OrderByDescending(s => s.StartedAt)
                .Take(20)
                .ToListAsync();
            return Ok(sessions);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] DriveSession session)
        {
            session.StartedAt = DateTime.UtcNow;
            _db.DriveSessions.Add(session);
            await _db.SaveChangesAsync();
            return Ok(session);
        }

        [HttpPut("{id}/end")]
        public async Task<IActionResult> End(int id, [FromBody] DriveSession update)
        {
            var session = await _db.DriveSessions.FindAsync(id);
            if (session == null) return NotFound();
            session.EndedAt = DateTime.UtcNow;
            session.TotalDistanceKm = update.TotalDistanceKm;
            session.AverageSpeedKmh = update.AverageSpeedKmh;
            session.SpeedConsistencyScore = update.SpeedConsistencyScore;
            session.SpeedAlertCount = update.SpeedAlertCount;
            session.DistractionAlertCount = update.DistractionAlertCount;
            session.BackingAlertCount = update.BackingAlertCount;
            session.LaneChangeAlertCount = update.LaneChangeAlertCount;
            session.MergeAlertCount = update.MergeAlertCount;
            session.ExitAlertCount = update.ExitAlertCount;
            await _db.SaveChangesAsync();
            return Ok(session);
        }
    }
}
