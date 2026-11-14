using System.Security.Cryptography;
using System.Text;
using Baton.Domain.Automations;
using Microsoft.AspNetCore.DataProtection;

namespace Baton.Persistence.Security;

/// <summary>
/// Encrypts a webhook notification channel's URL and signing secret (OBS-05, ADR-0018, design.md decision 16).
/// The API protects them when a channel is saved; the orchestrator reveals them to deliver. The signing secret
/// has the same shape as an inbound webhook secret, so receivers verify both the same way.
/// </summary>
public sealed class NotificationSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _url = provider.CreateProtector("Baton.Notifications.WebhookUrl.v1");
    private readonly IDataProtector _secret = provider.CreateProtector("Baton.Notifications.Secret.v1");

    public byte[] ProtectUrl(string url) => _url.Protect(Encoding.UTF8.GetBytes(url));

    /// <returns>The secret to show once, and what to store.</returns>
    public (string Secret, byte[] Protected) IssueSecret()
    {
        var secret = WebhookSignature.GenerateSecret();
        return (secret, _secret.Protect(Encoding.UTF8.GetBytes(secret)));
    }

    /// <returns><c>null</c> when the stored value cannot be decrypted (e.g. the key ring was lost).</returns>
    public string? RevealUrl(byte[] stored) => Reveal(_url, stored);

    /// <returns><c>null</c> when the stored value cannot be decrypted.</returns>
    public string? RevealSecret(byte[] stored) => Reveal(_secret, stored);

    private static string? Reveal(IDataProtector protector, byte[] stored)
    {
        try
        {
            return Encoding.UTF8.GetString(protector.Unprotect(stored));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
