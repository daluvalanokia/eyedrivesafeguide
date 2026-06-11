// ============================================================
// AddressesController.cs — Security-Hardened
// SECURITY FIXES:
//   AS-1  — [Authorize] added
//   OW-1  — All queries user-scoped (UserId filter)
//   DS-7  — AuditLogService called on mutations
//   AS-2  — Antiforgery handled by middleware; [FromBody] kept
// ============================================================
using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using EyeDriveGuide.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Controllers.Api;

[ApiController]
[Route("api/addresses")]
[Authorize]  // SECURITY FIX AS-1
public class AddressesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    private readonly ILogger<AddressesController> _logger;

    public AddressesController(
        AppDbContext db,
        AuditLogService audit,
        ILogger<AddressesController> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    // SECURITY FIX OW-1: only return addresses belonging to the current user
    private string UserId => User.Identity?.Name ?? "anonymous";

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.Addresses
            .Where(a => a.UserId == UserId)  // User-scoped
            .OrderBy(a => a.Type).ThenBy(a => a.Label)
            .Select(a => new {
                a.Id, a.Label, a.StreetAddress, a.City,
                a.State, a.ZipCode, a.Country, a.Type,
                a.Latitude, a.Longitude,
                FullAddress = a.StreetAddress +
                    (a.City != null ? ", " + a.City : "") +
                    (a.State != null ? ", " + a.State : "")
            })
            .ToListAsync();

        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        // SECURITY FIX OW-1: scope to current user
        var a = await _db.Addresses
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);

        if (a == null) return NotFound();
        return Ok(a);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Address address)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        address.CreatedAt = DateTime.UtcNow;
        address.UserId = UserId;  // SECURITY FIX OW-1: stamp owner

        _db.Addresses.Add(address);
        await _db.SaveChangesAsync();

        // SECURITY FIX DS-7: audit trail
        await _audit.LogAsync("ADDRESS_CREATE", "Address", address.Id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), UserId);

        _logger.LogInformation("Address created: {Id} by {User}", address.Id, UserId);
        return Ok(new { address.Id, address.Label, address.Type });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Address address)
    {
        if (id != address.Id) return BadRequest(new { error = "ID mismatch" });

        // SECURITY FIX OW-1: only allow update of own records
        var existing = await _db.Addresses
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);

        if (existing == null) return NotFound();

        existing.Label = address.Label;
        existing.StreetAddress = address.StreetAddress;
        existing.City = address.City;
        existing.State = address.State;
        existing.ZipCode = address.ZipCode;
        existing.Country = address.Country;
        existing.Type = address.Type;
        existing.Latitude = address.Latitude;
        existing.Longitude = address.Longitude;

        await _db.SaveChangesAsync();

        // SECURITY FIX DS-7: audit trail
        await _audit.LogAsync("ADDRESS_UPDATE", "Address", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), UserId);

        return Ok(existing);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        // SECURITY FIX OW-1: only allow delete of own records
        var a = await _db.Addresses
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);

        if (a == null) return NotFound();

        _db.Addresses.Remove(a);
        await _db.SaveChangesAsync();

        // SECURITY FIX DS-7: audit trail
        await _audit.LogAsync("ADDRESS_DELETE", "Address", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), UserId);

        return Ok(new { message = "Deleted" });
    }
}
