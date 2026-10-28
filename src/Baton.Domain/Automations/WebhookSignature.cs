using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Baton.Domain.Automations;

/// <summary>
/// Webhook authentication (AUT-04, design.md "Webhooks"): <c>X-Signature: sha256=&lt;hex HMAC-SHA256 of the raw
/// body&gt;</c>, keyed with the UTF-8 bytes of the trigger's secret. A secret is 32 random bytes, base64url,
/// prefixed <c>whsec_</c> so secret scanners recognize it.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Signature";
    public const string SecretPrefix = "whsec_";

    private const string Scheme = "sha256=";
    private const int SecretBytes = 32;

    public static string GenerateSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The header value a sender computes.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Scheme + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
    }

    /// <summary>Constant-time check of <paramref name="header"/> against the body's HMAC; a missing or malformed header fails.</summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? header)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (header is null || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var hex = header.AsSpan(Scheme.Length).Trim();
        if (hex.Length != HMACSHA256.HashSizeInBytes * 2)
            return false;

        Span<byte> presented = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (Convert.FromHexString(hex, presented, out _, out var written) != OperationStatus.Done || written != presented.Length)
            return false;

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
