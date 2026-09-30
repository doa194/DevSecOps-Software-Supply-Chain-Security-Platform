// Signed integration events: a consumer must reject any message that was altered, signed
// by an unknown key, attributed to the wrong publisher, or of a type its publisher may not
// emit. These are the checks that stop a compromised worker from forging business events.
using System.Security.Cryptography;
using Commerce.BuildingBlocks.Messaging;

namespace Commerce.UnitTests.Messaging;

public sealed class EnvelopeSigningTests : IDisposable
{
    private readonly ECDsa _apiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _workerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly EnvelopeVerifier _verifier;

    public EnvelopeSigningTests()
    {
        _verifier = new EnvelopeVerifier(new Dictionary<string, TrustedPublisher>
        {
            ["api-key"] = new() { Source = "commerce-api", PublicKeyPem = _apiKey.ExportSubjectPublicKeyInfoPem(), AllowedTypes = ["payments.payment-captured.v1"] },
            ["worker-key"] = new() { Source = "document-worker", PublicKeyPem = _workerKey.ExportSubjectPublicKeyInfoPem(), AllowedTypes = ["documents.document-generated.v1"] },
        });
    }

    private static MessageEnvelope Sign(ECDsa key, string keyId, string source, string type, string payload = """{"amount":10}""")
    {
        using var signer = new EnvelopeSigner(new SigningOptions { KeyId = keyId, PrivateKeyPem = key.ExportPkcs8PrivateKeyPem() }, source);
        return signer.Sign(Guid.NewGuid(), type, DateTimeOffset.UtcNow, "corr-1", null, payload);
    }

    [Fact]
    public void A_correctly_signed_message_is_accepted() =>
        Assert.Equal(VerificationFailure.None, _verifier.Verify(Sign(_apiKey, "api-key", "commerce-api", "payments.payment-captured.v1")));

    [Fact]
    public void A_modified_payload_is_rejected()
    {
        var envelope = Sign(_apiKey, "api-key", "commerce-api", "payments.payment-captured.v1") with { Payload = """{"amount":1000}""" };

        Assert.Equal(VerificationFailure.InvalidSignature, _verifier.Verify(envelope));
    }

    [Fact]
    public void A_modified_correlation_id_is_rejected()
    {
        var envelope = Sign(_apiKey, "api-key", "commerce-api", "payments.payment-captured.v1") with { CorrelationId = "other" };

        Assert.Equal(VerificationFailure.InvalidSignature, _verifier.Verify(envelope));
    }

    [Fact]
    public void A_message_signed_by_an_unknown_key_is_rejected()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal(VerificationFailure.UnknownKey, _verifier.Verify(Sign(attacker, "attacker-key", "commerce-api", "payments.payment-captured.v1")));
    }

    [Fact]
    public void A_worker_cannot_impersonate_the_api()
    {
        var envelope = Sign(_workerKey, "worker-key", "commerce-api", "payments.payment-captured.v1");

        Assert.Equal(VerificationFailure.SourceMismatch, _verifier.Verify(envelope));
    }

    [Fact]
    public void A_worker_cannot_emit_event_types_it_does_not_own()
    {
        var envelope = Sign(_workerKey, "worker-key", "document-worker", "payments.payment-captured.v1");

        Assert.Equal(VerificationFailure.TypeNotAllowedForPublisher, _verifier.Verify(envelope));
    }

    [Fact]
    public void A_malformed_signature_is_rejected()
    {
        var envelope = Sign(_apiKey, "api-key", "commerce-api", "payments.payment-captured.v1") with { Signature = "not base64!" };

        Assert.Equal(VerificationFailure.InvalidSignature, _verifier.Verify(envelope));
    }

    public void Dispose()
    {
        _apiKey.Dispose();
        _workerKey.Dispose();
        _verifier.Dispose();
    }
}
