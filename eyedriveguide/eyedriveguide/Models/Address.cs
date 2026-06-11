using System.ComponentModel.DataAnnotations;

namespace EyeDriveGuide.Models
{
    public enum AddressType { Home, Work, Frequent }

    public class Address
    {
        public int Id { get; set; }

        [Required]
        [Display(Name = "Label")]
        public string Label { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Street Address")]
        public string StreetAddress { get; set; } = string.Empty;

        public string? City { get; set; }
        public string? State { get; set; }
        public string? ZipCode { get; set; }
        public string? Country { get; set; }

        public AddressType Type { get; set; } = AddressType.Frequent;

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string FullAddress =>
            string.Join(", ", new[] { StreetAddress, City, State, ZipCode, Country }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}
