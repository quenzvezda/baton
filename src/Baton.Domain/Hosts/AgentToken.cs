using System.Security.Cryptography;
using System.Text;

namespace Baton.Domain.Hosts;

/// <summary>
/// Group-scoped agent credentials (ADR-0013): 32 random bytes, base64url, prefixed so secret scanners
/// recognize them. Only the SHA-256 of the whole token is stored.
/// </summary>
public static class AgentToken
{
    public const string Prefix = "baton_";

    private const int SecretBytes = 32;

    public static string Generate() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    public static byte[] Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
