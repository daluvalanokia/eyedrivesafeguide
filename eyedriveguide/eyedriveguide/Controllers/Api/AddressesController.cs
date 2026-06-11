using EyeDriveGuide.Data;
using EyeDriveGuide.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EyeDriveGuide.Controllers.Api
{
    [ApiController]
    [Route("api/addresses")]
    public class AddressesController : ControllerBase
    {
        private readonly AppDbContext _db;

        public AddressesController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var list = await _db.Addresses
                .OrderBy(a => a.Type).ThenBy(a => a.Label)
                .Select(a => new {
                    a.Id, a.Label, a.StreetAddress, a.City, a.State,
                    a.ZipCode, a.Country, a.Type, a.Latitude, a.Longitude,
                    FullAddress = a.StreetAddress + (a.City != null ? ", " + a.City : "") +
                                  (a.State != null ? ", " + a.State : "")
                })
                .ToListAsync();
            return Ok(list);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get(int id)
        {
            var a = await _db.Addresses.FindAsync(id);
            if (a == null) return NotFound();
            return Ok(a);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Address address)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            address.CreatedAt = DateTime.UtcNow;
            _db.Addresses.Add(address);
            await _db.SaveChangesAsync();
            return Ok(address);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] Address address)
        {
            if (id != address.Id) return BadRequest();
            var existing = await _db.Addresses.FindAsync(id);
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
            return Ok(existing);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var a = await _db.Addresses.FindAsync(id);
            if (a == null) return NotFound();
            _db.Addresses.Remove(a);
            await _db.SaveChangesAsync();
            return Ok();
        }
    }
}
