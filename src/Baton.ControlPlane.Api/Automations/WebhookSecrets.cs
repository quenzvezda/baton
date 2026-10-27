using System.Security.Cryptography;
using System.Text;
using Baton.Domain.Automations;
using Microsoft.AspNetCore.DataProtection;

namespace Baton.ControlPlane.Api.Automations;

/// <summary>
/// Issues webhook secrets and keeps them encrypted with Data Protection, whose key ring is in Postgres
/// (ADR-0015, design.md decision 2). The plain secret leaves the API only in the response that issues it.
/// </summary>
public sealed class WebhookSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Baton.Webhooks.Secret.v1");

    /// <returns>The secret to show once, and what to store.</returns>
    public (string Secret, byte[] Protected) Issue()
    {
        var secret = WebhookSignature.GenerateSecret();
        return (secret, _protector.Protect(Encoding.UTF8.GetBytes(secret)));
    }

    /// <returns><c>null</c> when the stored value cannot be decrypted (e.g. the key ring was lost).</returns>
    public string? Reveal(byte[] stored)
    {
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(stored));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
