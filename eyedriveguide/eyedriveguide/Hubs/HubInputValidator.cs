// ============================================================
// HubInputValidator.cs — SECURITY FIX AS-5
// Validates and sanitises all SignalR hub method inputs.
// Called at the top of each hub method before any processing.
// ============================================================
namespace EyeDriveGuide.Hubs;

public static class HubInputValidator
{
    // Valid geographic bounds
    private const double MinLat = -90.0;
    private const double MaxLat = 90.0;
    private const double MinLng = -180.0;
    private const double MaxLng = 180.0;

    // Reasonable driving speed bounds
    private const double MaxSpeedKmh = 350.0; // Faster than any road vehicle
    private const double MinSpeedKmh = 0.0;

    // Reasonable audio bounds
    private const double MinDbLevel = 0.0;
    private const double MaxDbLevel = 200.0; // Physically impossible to exceed

    // Max string lengths for free-text hub parameters
    private const int MaxModeLength = 32;
    private const int MaxAddressLength = 500;

    public record PositionValidationResult(bool IsValid, string? Error,
        double Lat = 0, double Lng = 0, double SpeedKmh = 0, double? DbLevel = null);

    /// <summary>Validates UpdatePosition hub method parameters.</summary>
    public static PositionValidationResult ValidatePosition(
        double lat, double lng, double speedKmh,
        double? accelMagnitude, double? dbLevel)
    {
        if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < MinLat || lat > MaxLat)
            return new PositionValidationResult(false, $"Invalid latitude: {lat}");

        if (double.IsNaN(lng) || double.IsInfinity(lng) || lng < MinLng || lng > MaxLng)
            return new PositionValidationResult(false, $"Invalid longitude: {lng}");

        if (double.IsNaN(speedKmh) || double.IsInfinity(speedKmh))
            return new PositionValidationResult(false, $"Invalid speed: {speedKmh}");

        // Clamp speed to valid range rather than rejecting (GPS glitches)
        var clampedSpeed = Math.Clamp(speedKmh, MinSpeedKmh, MaxSpeedKmh);

        // Clamp dB level; don't reject — microphone hardware can produce odd values
        double? clampedDb = dbLevel.HasValue
            ? Math.Clamp(dbLevel.Value, MinDbLevel, MaxDbLevel)
            : null;

        // Reject clearly absurd accel magnitudes (> 100g)
        if (accelMagnitude.HasValue &&
            (double.IsNaN(accelMagnitude.Value) || accelMagnitude.Value > 981.0))
            return new PositionValidationResult(false, "Invalid accelerometer magnitude");

        return new PositionValidationResult(true, null, lat, lng, clampedSpeed, clampedDb);
    }

    /// <summary>Validates StartSession parameters.</summary>
    public static (bool IsValid, string? Error) ValidateStartSession(
        string mode, string? destinationAddress)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return (false, "Mode is required");

        if (mode.Length > MaxModeLength)
            return (false, $"Mode exceeds maximum length of {MaxModeLength}");

        var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "JustDrive", "destination" };

        if (!allowedModes.Contains(mode))
            return (false, $"Invalid mode '{mode}'");

        if (destinationAddress != null && destinationAddress.Length > MaxAddressLength)
            return (false, $"Destination address exceeds maximum length of {MaxAddressLength}");

        return (true, null);
    }

    /// <summary>Validates LoadRoute coordinate parameters.</summary>
    public static (bool IsValid, string? Error) ValidateLoadRoute(
        double startLat, double startLng, double endLat, double endLng)
    {
        if (!IsValidCoord(startLat, startLng))
            return (false, "Invalid start coordinates");

        if (!IsValidCoord(endLat, endLng))
            return (false, "Invalid end coordinates");

        // Reject same-point routes
        if (Math.Abs(startLat - endLat) < 0.0001 && Math.Abs(startLng - endLng) < 0.0001)
            return (false, "Start and end coordinates are identical");

        return (true, null);
    }

    /// <summary>Validates UpdateLane parameter.</summary>
    public static (bool IsValid, string? Error) ValidateUpdateLane(int laneIndex)
    {
        if (laneIndex < 0 || laneIndex > 10) // Max 10 lanes on any real road
            return (false, $"Invalid lane index: {laneIndex}");

        return (true, null);
    }

    /// <summary>HTML-encodes a string for safe display.</summary>
    public static string SanitiseForDisplay(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return System.Net.WebUtility.HtmlEncode(input);
    }

    private static bool IsValidCoord(double lat, double lng) =>
        !double.IsNaN(lat) && !double.IsInfinity(lat) &&
        !double.IsNaN(lng) && !double.IsInfinity(lng) &&
        lat >= MinLat && lat <= MaxLat &&
        lng >= MinLng && lng <= MaxLng;
}
