using System.Text;
using Baton.Domain.Automations;

namespace Baton.Domain.Tests.Automations;

/// <summary>AUT-04: webhook bodies are signed with HMAC-SHA256 of the trigger's secret.</summary>
public sealed class WebhookSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"event":"orders.closed"}""");

    [Fact]
    public void Signature_is_hex_hmac_sha256_of_the_body_keyed_by_the_secret()
    {
        // Reference value: printf '%s' '{"event":"orders.closed"}' | openssl dgst -sha256 -hmac 'whsec_test'
        Assert.Equal("sha256=b341647487e1694c87927a1024b3d093991fd289816f0d3bd2fd2d9cf2bec9a2",
            WebhookSignature.Compute("whsec_test", Body));
    }

    [Fact]
    public void Correct_signature_verifies_in_either_hex_case()
    {
        var header = WebhookSignature.Compute("whsec_test", Body);

        Assert.True(WebhookSignature.Verify("whsec_test", Body, header));
        Assert.True(WebhookSignature.Verify("whsec_test", Body, "SHA256=" + header[7..].ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha1=0123")]
    [InlineData("sha256=zz")]
    public void Missing_or_malformed_signature_fails(string? header)
    {
        Assert.False(WebhookSignature.Verify("whsec_test", Body, header));
    }

    [Fact]
    public void Signature_with_another_secret_or_body_fails()
    {
        var header = WebhookSignature.Compute("whsec_other", Body);

        Assert.False(WebhookSignature.Verify("whsec_test", Body, header));
        Assert.False(WebhookSignature.Verify("whsec_other", Encoding.UTF8.GetBytes("{}"), header));
    }

    [Fact]
    public void Secrets_are_prefixed_and_unique()
    {
        var first = WebhookSignature.GenerateSecret();

        Assert.StartsWith(WebhookSignature.SecretPrefix, first, StringComparison.Ordinal);
        Assert.Equal(WebhookSignature.SecretPrefix.Length + 43, first.Length);
        Assert.NotEqual(first, WebhookSignature.GenerateSecret());
    }
}
