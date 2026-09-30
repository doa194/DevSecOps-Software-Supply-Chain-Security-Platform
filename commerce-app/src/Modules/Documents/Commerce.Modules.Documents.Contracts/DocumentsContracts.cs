// Public contract of the Documents module and the document worker.
// The API requests invoice generation; only the document worker may report the result
// (its signing key is the only one allowed to emit the two result events).
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Documents.Contracts;

public sealed record InvoiceLineV1(string Sku, string Name, int Quantity, decimal UnitPrice);

[IntegrationEvent("documents.generation-requested", 1)]
public sealed record DocumentGenerationRequestedV1(
    Guid DocumentId,
    Guid OrderId,
    Guid CustomerId,
    string Kind,
    IReadOnlyList<InvoiceLineV1> Lines,
    decimal Total,
    string Currency) : IntegrationEvent;

[IntegrationEvent("documents.document-generated", 1)]
public sealed record DocumentGeneratedV1(Guid DocumentId, string StorageKey, string Sha256, long SizeBytes, string ContentType) : IntegrationEvent;

[IntegrationEvent("documents.generation-failed", 1)]
public sealed record DocumentGenerationFailedV1(Guid DocumentId, string Reason) : IntegrationEvent;
