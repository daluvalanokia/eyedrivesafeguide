// ============================================================
// Address.cs — Security-Updated (merged, single definition)
// REPLACES both Address.cs and Address_UserScoped.cs
// SECURITY FIXES:
//   OW-1 — UserId for row-level security
//   DS-2 — MaxLength annotations; encryption applied in AppDbContext
// ============================================================
using System.ComponentModel.DataAnnotations;

namespace EyeDriveGuide.Models;

public enum AddressType { Home, Work, Frequent }

public class Address
{
    public int Id { get; set; }

    // SECURITY FIX OW-1: owner identifier for user-scoped queries
    public string? UserId { get; set; }

    [Required, MaxLength(200)]
    public string Label { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    // SECURITY FIX DS-2: encrypted at rest via EncryptedStringConverter in AppDbContext
    public string StreetAddress { get; set; } = string.Empty;

    [MaxLength(200)] public string? City    { get; set; }
    [MaxLength(100)] public string? State   { get; set; }
    [MaxLength(20)]  public string? ZipCode { get; set; }
    [MaxLength(100)] public string? Country { get; set; }

    public AddressType Type { get; set; } = AddressType.Frequent;

    [Range(-90,  90)]  public double? Latitude  { get; set; }
    [Range(-180, 180)] public double? Longitude { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string FullAddress => string.Join(", ",
        new[] { StreetAddress, City, State, ZipCode, Country }
        .Where(s => !string.IsNullOrWhiteSpace(s)));
}
