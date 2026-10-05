using FluentAssertions;
using Inbox.Infrastructure.Channels.WhatsApp;
using Xunit;

namespace Inbox.Infrastructure.Tests;

/// <summary>
/// The signature check is the only thing authenticating a public endpoint, so the tests cover the
/// shapes an attacker (or a misconfigured Meta) would actually send.
/// </summary>
public sealed class WhatsAppSignatureTests
{
    private const string Secret = "app-secret-value";
    private const string Body = """{"entry":[{"changes":[{"value":{}}]}]}""";

    [Fact]
    public void A_signature_made_with_the_app_secret_is_accepted()
    {
        var signature = WhatsAppSignature.Sign(Body, Secret);

        WhatsAppSignature.Verify(Body, signature, Secret).Should().BeTrue();
    }

    [Fact]
    public void The_signature_is_a_lowercase_sha256_hex_digest()
    {
        var signature = WhatsAppSignature.Sign(Body, Secret);

        signature.Should().StartWith("sha256=");
        signature["sha256=".Length..].Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Any_change_to_the_body_invalidates_it()
    {
        var signature = WhatsAppSignature.Sign(Body, Secret);

        // This is why the endpoint must not re-serialise the JSON before verifying.
        WhatsAppSignature.Verify(Body + " ", signature, Secret).Should().BeFalse();
        WhatsAppSignature.Verify(Body.Replace("{", "{ "), signature, Secret).Should().BeFalse();
    }

    [Fact]
    public void A_signature_from_a_different_secret_is_rejected()
    {
        var signature = WhatsAppSignature.Sign(Body, "another-secret");

        WhatsAppSignature.Verify(Body, signature, Secret).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abc")]
    [InlineData("1a2b3c")]
    [InlineData("sha256=not-hex-at-all")]
    [InlineData("sha256=ZZZ")]
    public void Malformed_headers_are_rejected_without_throwing(string? header)
    {
        // The header is attacker-controlled, so this is an expected input rather than a fault.
        Action verify = () => WhatsAppSignature.Verify(Body, header, Secret);

        verify.Should().NotThrow();
        WhatsAppSignature.Verify(Body, header, Secret).Should().BeFalse();
    }

    [Fact]
    public void A_truncated_digest_is_rejected()
    {
        var signature = WhatsAppSignature.Sign(Body, Secret)[..40];

        WhatsAppSignature.Verify(Body, signature, Secret).Should().BeFalse();
    }

    [Fact]
    public void An_uppercase_digest_is_accepted()
    {
        // Hex case carries no meaning, and a proxy rewriting the header must not break delivery.
        var signature = "sha256=" + WhatsAppSignature.Sign(Body, Secret)["sha256=".Length..].ToUpperInvariant();

        WhatsAppSignature.Verify(Body, signature, Secret).Should().BeTrue();
    }

    [Fact]
    public void Without_a_secret_nothing_can_be_verified()
    {
        var signature = WhatsAppSignature.Sign(Body, Secret);

        // A channel with no app secret configured is misconfigured, not open.
        WhatsAppSignature.Verify(Body, signature, string.Empty).Should().BeFalse();
    }
}