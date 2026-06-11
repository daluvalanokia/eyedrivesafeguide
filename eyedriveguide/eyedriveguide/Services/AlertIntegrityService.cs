// ============================================================
// AlertIntegrityService.cs — SECURITY FIX OW-5
// Signs all server-originated alerts with HMAC-SHA256.
// Client (alert-system.js) verifies signature before rendering.
// Prevents alert injection by a compromised SignalR client.
// ============================================================
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EyeDriveGuide.Services;

public class AlertIntegrityService
{
    private readonly byte[] _signingKey;
    private readonly ILogger<AlertIntegrityService> _logger;

    public AlertIntegrityService(IConfiguration config, ILogger<AlertIntegrityService> logger)
    {
        _logger = logger;

        // Key loaded from env var or user-secrets — never hardcoded
        var keyStr = config["Alert__SigningKey"]
                  ?? Environment.GetEnvironmentVariable("EDG_ALERT_SIGNING_KEY");

        if (string.IsNullOrWhiteSpace(keyStr))
        {
            // Generate ephemeral key for this process lifetime
            // (alerts from previous sessions won't verify after restart — acceptable)
            _signingKey = new byte[32];
            RandomNumberGenerator.Fill(_signingKey);
            _logger.LogWarning("Alert signing key not configured — using ephemeral key. " +
                "Set EDG_ALERT_SIGNING_KEY for persistent signing.");
        }
        else
        {
            // Derive a 256-bit key from the configured string
            _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(keyStr));
        }
    }

    /// <summary>
    /// Wraps an alert payload with a server timestamp and HMAC signature.
    /// </summary>
    public object Sign(object alertPayload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payloadJson = JsonSerializer.Serialize(alertPayload);

        // HMAC over "timestamp:payloadJson"
        var dataToSign = $"{timestamp}:{payloadJson}";
        var hmac = ComputeHmac(dataToSign);

        return new
        {
            payload = alertPayload,
            timestamp,
            sig = hmac
        };
    }

    /// <summary>
    /// Verifies a signed alert received from the network.
    /// Used server-side if alerts are ever relayed between hubs.
    /// </summary>
    public bool Verify(object alertPayload, long timestamp, string signature)
    {
        // Reject alerts older than 30 seconds (replay protection)
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp;
        if (age > 30 || age < -5)
        {
            _logger.LogWarning("Alert replay attempt: timestamp age {Age}s", age);
            return false;
        }

        var payloadJson = JsonSerializer.Serialize(alertPayload);
        var dataToVerify = $"{timestamp}:{payloadJson}";
        var expected = ComputeHmac(dataToVerify);

        // Constant-time comparison to prevent timing attacks
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    private string ComputeHmac(string data)
    {
        using var hmac = new HMACSHA256(_signingKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }
}
