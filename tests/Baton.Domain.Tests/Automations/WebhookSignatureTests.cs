using System.Text;
using Baton.Domain.Automations;

namespace Baton.Domain.Tests.Automations;

/// <summary>AUT-04 (harden-webhook-signatures): HMAC-SHA256 over timestamp, delivery id and body, 5-minute window.</summary>
public sealed class WebhookSignatureTests
{
    private const long SignedAt = 1_763_100_000;
    private const string Delivery = "delivery-1";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"event":"orders.closed"}""");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(SignedAt);

    [Fact]
    public void Signature_is_hex_hmac_sha256_of_timestamp_delivery_id_and_body()
    {
        // Reference value, as in the README:
        // printf '%s.%s.%s' 1763100000 delivery-1 '{"event":"orders.closed"}' | openssl dgst -sha256 -hmac 'whsec_test'
        Assert.Equal("t=1763100000,v1=3789812c2cece311baa958c8bb55d19d617d7d4165e2d74d88e9026554ad37f1",
            WebhookSignature.Compute("whsec_test", SignedAt, Delivery, Body));
    }

    [Fact]
    public void Correct_signature_verifies_in_either_hex_case_and_with_spaces()
    {
        var header = WebhookSignature.Compute("whsec_test", SignedAt, Delivery, Body);
        var hex = header[(header.IndexOf("v1=", StringComparison.Ordinal) + 3)..];

        Assert.True(WebhookSignature.Verify("whsec_test", Body, Delivery, header, Now));
        Assert.True(WebhookSignature.Verify("whsec_test", Body, Delivery, $"t={SignedAt}, v1={hex.ToUpperInvariant()}", Now));
    }

    [Theory]
    [InlineData(-300, true)]
    [InlineData(300, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    [InlineData(-360, false)]
    public void Timestamp_must_be_within_five_minutes_of_server_time(int skewSeconds, bool valid)
    {
        var header = WebhookSignature.Compute("whsec_test", SignedAt + skewSeconds, Delivery, Body);

        Assert.Equal(valid, WebhookSignature.Verify("whsec_test", Body, Delivery, header, Now));
    }

    [Fact]
    public void Signature_does_not_verify_for_another_delivery_id_secret_or_body()
    {
        var header = WebhookSignature.Compute("whsec_test", SignedAt, Delivery, Body);

        Assert.False(WebhookSignature.Verify("whsec_test", Body, "delivery-2", header, Now));
        Assert.False(WebhookSignature.Verify("whsec_test", Body, null, header, Now));
        Assert.False(WebhookSignature.Verify("whsec_other", Body, Delivery, header, Now));
        Assert.False(WebhookSignature.Verify("whsec_test", Encoding.UTF8.GetBytes("{}"), Delivery, header, Now));
    }

    [Fact]
    public void Missing_delivery_id_is_signed_as_empty()
    {
        var header = WebhookSignature.Compute("whsec_test", SignedAt, "", Body);

        Assert.True(WebhookSignature.Verify("whsec_test", Body, null, header, Now));
    }

    [Fact]
    public void Any_of_several_signatures_may_match_during_a_rotation()
    {
        var old = WebhookSignature.Compute("whsec_old", SignedAt, Delivery, Body);
        var current = WebhookSignature.Compute("whsec_new", SignedAt, Delivery, Body);
        var both = $"{old},{current[(current.IndexOf(',', StringComparison.Ordinal) + 1)..]}";

        Assert.True(WebhookSignature.Verify("whsec_new", Body, Delivery, both, Now));
        Assert.True(WebhookSignature.Verify("whsec_old", Body, Delivery, both, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=3789812c2cece311baa958c8bb55d19d617d7d4165e2d74d88e9026554ad37f1")] // the old, body-only form
    [InlineData("v1=3789812c2cece311baa958c8bb55d19d617d7d4165e2d74d88e9026554ad37f1")]
    [InlineData("t=1763100000")]
    [InlineData("t=1763100000,v1=zz")]
    [InlineData("t=-5,v1=3789812c2cece311baa958c8bb55d19d617d7d4165e2d74d88e9026554ad37f1")]
    [InlineData("t=1763100000,t=1763100000,v1=3789812c2cece311baa958c8bb55d19d617d7d4165e2d74d88e9026554ad37f1")]
    [InlineData("t=1763100000,garbage,v1=3789812c2cece311baa958c8bb55d19d617d7d4165e2d74d88e9026554ad37f1")]
    public void Missing_or_malformed_header_fails(string? header)
    {
        Assert.False(WebhookSignature.Verify("whsec_test", Body, Delivery, header, Now));
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var header = WebhookSignature.Compute("whsec_test", SignedAt, Delivery, Body) + ",v0=abc";

        Assert.True(WebhookSignature.Verify("whsec_test", Body, Delivery, header, Now));
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
