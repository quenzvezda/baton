using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Baton.Persistence.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.Security;

/// <summary>G1 security review M3, ADR-0015: the key ring is encrypted at rest when a certificate is configured.</summary>
public sealed class KeyRingProtectionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("baton-keyring-");

    [Fact]
    public void Configured_certificate_encrypts_the_key_ring_and_reads_it_back()
    {
        var pfx = Path.Combine(_dir.FullName, "keyring.pfx");
        File.WriteAllBytes(pfx, SelfSignedPfx("test-password"));
        var keys = _dir.CreateSubdirectory("keys");
        var configuration = Configuration(pfx, "test-password");

        var protectedText = Protector(keys, configuration, out var enabled).Protect("whsec_example");

        Assert.True(enabled);
        var xml = File.ReadAllText(Assert.Single(keys.GetFiles("key-*.xml")).FullName);
        Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("unencrypted form", xml, StringComparison.Ordinal);
        // A fresh process (new service provider) decrypts the stored key with the same certificate file.
        Assert.Equal("whsec_example", Protector(keys, configuration, out _).Unprotect(protectedText));
    }

    [Fact]
    public void Without_a_certificate_the_key_ring_stays_unencrypted()
    {
        var keys = _dir.CreateSubdirectory("plain");

        Protector(keys, new ConfigurationBuilder().Build(), out var enabled).Protect("whsec_example");

        Assert.False(enabled);
        var xml = File.ReadAllText(Assert.Single(keys.GetFiles("key-*.xml")).FullName);
        Assert.Contains("unencrypted form", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificate_without_a_private_key_is_refused()
    {
        var cer = Path.Combine(_dir.FullName, "public.pfx");
        using (var cert = X509CertificateLoader.LoadPkcs12(SelfSignedPfx(null), null))
        {
            using var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
            File.WriteAllBytes(cer, publicOnly.Export(X509ContentType.Pkcs12));
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
            Protector(_dir.CreateSubdirectory("nokey"), Configuration(cer, null), out _));

        Assert.Contains("no private key", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static IDataProtector Protector(DirectoryInfo keys, IConfiguration configuration, out bool enabled)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection().SetApplicationName("baton").PersistKeysToFileSystem(keys);
        enabled = builder.ProtectKeysFromConfiguration(configuration);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("Baton.Webhooks.Secret.v1");
    }

    private static IConfiguration Configuration(string path, string? password) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KeyRingProtection.CertificatePathKey] = path,
            [KeyRingProtection.CertificatePasswordKey] = password,
        }).Build();

    private static byte[] SelfSignedPfx(string? password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=baton-keyring-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return cert.Export(X509ContentType.Pkcs12, password);
    }
}
