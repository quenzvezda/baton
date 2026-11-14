using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Baton.Domain.Automations;

/// <summary>
/// Webhook authentication (AUT-04; harden-webhook-signatures design.md):
/// <c>X-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256&gt;</c>, keyed with the UTF-8 bytes of the
/// trigger's secret, over <c>&lt;t&gt;.&lt;delivery id&gt;.&lt;raw body&gt;</c>. Signing the time and the
/// delivery id stops replays: a timestamp more than <see cref="Tolerance"/> from the server's time fails,
/// and inside the window the signed delivery id makes a replay a duplicate delivery. A secret is 32 random
/// bytes, base64url, prefixed <c>whsec_</c> so secret scanners recognize it.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Signature";
    public const string SecretPrefix = "whsec_";

    /// <summary>How far a signed timestamp may be from the server's time, either side.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private const string TimestampKey = "t";
    private const string SignatureKey = "v1";
    private const int SecretBytes = 32;
    private const int MaxSignatures = 4;

    public static string GenerateSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The header value a sender computes for a delivery signed at <paramref name="timestamp"/>.</summary>
    public static string Compute(string secret, long timestamp, string deliveryId, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(deliveryId);

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        Hash(secret, timestamp, deliveryId, body, mac);
        return string.Create(CultureInfo.InvariantCulture, $"{TimestampKey}={timestamp},{SignatureKey}={Convert.ToHexStringLower(mac)}");
    }

    /// <summary>
    /// Constant-time check of <paramref name="header"/> against the delivery: a missing or malformed header,
    /// a timestamp outside <see cref="Tolerance"/> of <paramref name="now"/> (Postgres time) or no matching
    /// <c>v1</c> fails.
    /// </summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? deliveryId, string? header, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!TryParse(header, out var timestamp, out var signatures))
            return false;

        var skew = Math.Abs(now.ToUnixTimeSeconds() - timestamp);
        if (skew > (long)Tolerance.TotalSeconds)
            return false;

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        Hash(secret, timestamp, deliveryId ?? string.Empty, body, expected);

        var matched = false;
        foreach (var presented in signatures)
            matched |= CryptographicOperations.FixedTimeEquals(expected, presented);
        return matched;
    }

    private static void Hash(string secret, long timestamp, string deliveryId, ReadOnlySpan<byte> body, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{deliveryId}.")));
        hmac.AppendData(body);
        hmac.GetHashAndReset(destination);
    }

    /// <summary>Exactly one <c>t</c> (unix seconds) and 1–4 well-formed <c>v1</c>; other keys are ignored.</summary>
    private static bool TryParse(string? header, out long timestamp, out List<byte[]> signatures)
    {
        timestamp = 0;
        signatures = [];
        if (string.IsNullOrWhiteSpace(header))
            return false;

        var timestamps = 0;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                return false;

            var key = part[..equals];
            var value = part[(equals + 1)..];
            if (key == TimestampKey)
            {
                if (++timestamps > 1 || value.Length is 0 or > 12 || !value.All(char.IsAsciiDigit)
                    || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
                {
                    return false;
                }
            }
            else if (key == SignatureKey)
            {
                var bytes = new byte[HMACSHA256.HashSizeInBytes];
                if (value.Length != bytes.Length * 2 || signatures.Count == MaxSignatures
                    || Convert.FromHexString(value, bytes, out _, out var written) != OperationStatus.Done || written != bytes.Length)
                {
                    return false;
                }

                signatures.Add(bytes);
            }
        }

        return timestamps == 1 && signatures.Count > 0;
    }
}
