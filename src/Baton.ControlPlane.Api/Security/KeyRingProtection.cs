using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace Baton.ControlPlane.Api.Security;

/// <summary>
/// Encrypts the Data Protection key ring at rest with a certificate when
/// <c>Baton:DataProtection:CertificatePath</c> is set (ADR-0015 "through configuration"; G1 security
/// review M3). Without it the keys sit unencrypted in <c>data_protection_keys</c>, next to the webhook
/// secrets they protect, and Data Protection logs a warning at startup. The certificate (PKCS#12, with
/// private key) and its password come from the deployment — a mounted file and an environment variable
/// such as <c>Baton__DataProtection__CertificatePassword</c> — never from the repository.
/// </summary>
public static class KeyRingProtection
{
    public const string CertificatePathKey = "Baton:DataProtection:CertificatePath";
    public const string CertificatePasswordKey = "Baton:DataProtection:CertificatePassword";

    /// <returns>Whether the key ring is protected with a certificate.</returns>
    public static bool ProtectKeysFromConfiguration(this IDataProtectionBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var path = configuration[CertificatePathKey];
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration[CertificatePasswordKey]);
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException($"The certificate at {CertificatePathKey} has no private key; the key ring could not be read back.");

        // Unprotect with the same loaded certificate too: by default Data Protection looks for the decrypting
        // certificate in the machine's certificate store, which a mounted file is not in.
        builder.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
        return true;
    }
}
