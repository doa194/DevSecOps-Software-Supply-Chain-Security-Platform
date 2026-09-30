// Signed internal integration events.
//
// Every publisher (the API and each worker that publishes) has its own ECDSA P-256 key
// pair. Publishers sign an envelope with their private key; consumers hold only public
// keys. Each public key is bound to one publisher name and to the event types that
// publisher is allowed to emit. A consumer therefore rejects a message when:
//   - the signature does not match the content (tampering in transit or at rest),
//   - the key is unknown (a forged publisher),
//   - the key belongs to a different publisher than the envelope claims (impersonation),
//   - the publisher is not allowed to emit that event type (a compromised worker trying to
//     fake, say, a payment confirmation).
// Anyone with broker credentials can still put bytes on a queue, but cannot make a
// consumer act on them.
using System.Security.Cryptography;
using System.Text;

namespace Commerce.BuildingBlocks.Messaging;

public sealed class SigningOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
}

public sealed class TrustedPublisher
{
    public string Source { get; set; } = string.Empty;
    public string PublicKeyPem { get; set; } = string.Empty;

    // Routing keys this publisher may emit; "*" is not supported on purpose.
    public List<string> AllowedTypes { get; set; } = [];
}

public enum VerificationFailure
{
    None,
    UnknownKey,
    SourceMismatch,
    TypeNotAllowedForPublisher,
    InvalidSignature,
}

public static class EnvelopeCanonicalForm
{
    // Every field that influences processing is covered by the signature. The payload is
    // represented by its SHA-256 so the signed string stays small.
    public static byte[] Bytes(Guid messageId, string type, string source, DateTimeOffset occurredAt, string? correlationId, string payload)
    {
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var canonical = string.Join('\n',
            "sscp-envelope-v1",
            messageId.ToString("D"),
            type,
            source,
            occurredAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            correlationId ?? string.Empty,
            payloadHash);
        return Encoding.UTF8.GetBytes(canonical);
    }
}

public sealed class EnvelopeSigner : IDisposable
{
    private readonly ECDsa _key;
    private readonly string _keyId;
    private readonly string _source;

    public EnvelopeSigner(SigningOptions options, string source)
    {
        if (string.IsNullOrWhiteSpace(options.KeyId) || string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            throw new InvalidOperationException("Messaging:Signing:KeyId and PrivateKeyPem must be configured to publish events.");
        }

        _key = ECDsa.Create();
        _key.ImportFromPem(options.PrivateKeyPem);
        _keyId = options.KeyId;
        _source = source;
    }

    public MessageEnvelope Sign(Guid messageId, string type, DateTimeOffset occurredAt, string? correlationId, string? traceParent, string payload)
    {
        var data = EnvelopeCanonicalForm.Bytes(messageId, type, _source, occurredAt, correlationId, payload);
        return new MessageEnvelope
        {
            MessageId = messageId,
            Type = type,
            Source = _source,
            OccurredAt = occurredAt,
            CorrelationId = correlationId,
            TraceParent = traceParent,
            Payload = payload,
            KeyId = _keyId,
            Signature = Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256)),
        };
    }

    public void Dispose() => _key.Dispose();
}

public sealed class EnvelopeVerifier : IDisposable
{
    private readonly Dictionary<string, (TrustedPublisher Publisher, ECDsa Key)> _publishers;

    public EnvelopeVerifier(IReadOnlyDictionary<string, TrustedPublisher> trustedPublishers)
    {
        _publishers = trustedPublishers.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var key = ECDsa.Create();
                key.ImportFromPem(pair.Value.PublicKeyPem);
                return (pair.Value, key);
            },
            StringComparer.Ordinal);
    }

    public VerificationFailure Verify(MessageEnvelope envelope)
    {
        if (!_publishers.TryGetValue(envelope.KeyId, out var trusted))
        {
            return VerificationFailure.UnknownKey;
        }

        if (!string.Equals(trusted.Publisher.Source, envelope.Source, StringComparison.Ordinal))
        {
            return VerificationFailure.SourceMismatch;
        }

        if (!trusted.Publisher.AllowedTypes.Contains(envelope.Type, StringComparer.Ordinal))
        {
            return VerificationFailure.TypeNotAllowedForPublisher;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            return VerificationFailure.InvalidSignature;
        }

        var data = EnvelopeCanonicalForm.Bytes(envelope.MessageId, envelope.Type, envelope.Source, envelope.OccurredAt, envelope.CorrelationId, envelope.Payload);
        return trusted.Key.VerifyData(data, signature, HashAlgorithmName.SHA256)
            ? VerificationFailure.None
            : VerificationFailure.InvalidSignature;
    }

    public void Dispose()
    {
        foreach (var (_, key) in _publishers.Values)
        {
            key.Dispose();
        }
    }
}
