// ============================================================
// EncryptedStringConverter.cs — SECURITY FIX DS-2
// EF Core value converter: transparently encrypts sensitive
// string fields (StreetAddress, DestinationAddress, Label)
// using AES-256-GCM before writing to SQLite.
//
// Key source: environment variable EDG_DB_ENCRYPT_KEY (base64)
// Generate: openssl rand -base64 32
// ============================================================
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;
using System.Text;

namespace EyeDriveGuide.Models;

public class EncryptedStringConverter : ValueConverter<string?, string?>
{
    public EncryptedStringConverter(string base64Key)
        : base(
            plaintext => Encrypt(plaintext, base64Key),
            ciphertext => Decrypt(ciphertext, base64Key))
    { }

    private static string? Encrypt(string? plaintext, string base64Key)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var key = Convert.FromBase64String(base64Key);
        if (key.Length != 32)
            throw new InvalidOperationException("EDG_DB_ENCRYPT_KEY must be 32 bytes (base64)");

        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Format: base64(nonce) + "." + base64(tag) + "." + base64(ciphertext)
        return $"enc:{Convert.ToBase64String(nonce)}" +
               $".{Convert.ToBase64String(tag)}" +
               $".{Convert.ToBase64String(ciphertext)}";
    }

    private static string? Decrypt(string? stored, string base64Key)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith("enc:")) return stored; // Legacy unencrypted value

        var key = Convert.FromBase64String(base64Key);
        var parts = stored[4..].Split('.');

        if (parts.Length != 3)
            throw new InvalidOperationException("Malformed encrypted value");

        var nonce = Convert.FromBase64String(parts[0]);
        var tag = Convert.FromBase64String(parts[1]);
        var ciphertext = Convert.FromBase64String(parts[2]);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Loads the encryption key from environment variables.
    /// Throws clearly if not configured, rather than silently storing plaintext.
    /// </summary>
    public static string LoadKeyFromEnvironment(IConfiguration config)
    {
        var key = config["EDG_DB_ENCRYPT_KEY"]
               ?? Environment.GetEnvironmentVariable("EDG_DB_ENCRYPT_KEY");

        if (string.IsNullOrWhiteSpace(key))
        {
            // In development: generate and warn. In production: hard fail.
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (env == "Production")
                throw new InvalidOperationException(
                    "SECURITY: EDG_DB_ENCRYPT_KEY must be set in production. " +
                    "Generate with: openssl rand -base64 32");

            // Development fallback (NOT for production)
            var devKey = new byte[32];
            RandomNumberGenerator.Fill(devKey);
            return Convert.ToBase64String(devKey);
        }

        var decoded = Convert.FromBase64String(key);
        if (decoded.Length != 32)
            throw new InvalidOperationException("EDG_DB_ENCRYPT_KEY must be 32 bytes (base64)");

        return key;
    }
}
