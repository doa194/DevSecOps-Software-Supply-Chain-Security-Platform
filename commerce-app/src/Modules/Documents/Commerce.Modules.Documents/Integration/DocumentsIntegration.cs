// Invoice generation flow:
//   OrderConfirmed           -> create a Requested invoice and ask the document worker
//   DocumentGenerated        -> the worker stored it; mark Available
//   DocumentGenerationFailed -> mark Failed
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Administration.Contracts;
using Commerce.Modules.Documents.Contracts;
using Commerce.Modules.Documents.Data;
using Commerce.Modules.Documents.Domain;
using Commerce.Modules.Orders.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;

namespace Commerce.Modules.Documents.Integration;

internal sealed class RequestInvoiceForConfirmedOrder(DocumentsDbContext db, Outbox<DocumentsDbContext> outbox, IFeatureManager features, TimeProvider clock)
    : IIntegrationEventHandler<OrderConfirmedV1>
{
    public async Task HandleAsync(OrderConfirmedV1 order, MessageContext context, CancellationToken cancellationToken)
    {
        if (!await features.IsEnabledAsync(FeatureFlags.DocumentsInvoiceGeneration, cancellationToken))
        {
            return;
        }

        if (await db.Documents.AnyAsync(d => d.OrderId == order.OrderId && d.Kind == DocumentKind.Invoice, cancellationToken))
        {
            return;
        }

        var invoice = Document.RequestInvoice(order.CustomerId, order.OrderId, clock.GetUtcNow());
        db.Documents.Add(invoice);
        // The request carries everything the worker needs, so the worker never reads the
        // Orders or Documents tables.
        outbox.Add(new DocumentGenerationRequestedV1(
            invoice.Id, order.OrderId, order.CustomerId, DocumentKind.Invoice.ToString(),
            order.Lines.Select(l => new InvoiceLineV1(l.Sku, l.Name, l.Quantity, l.UnitPrice)).ToList(), order.Total, order.Currency));
    }
}

internal sealed class RecordGeneratedDocument(DocumentsDbContext db) : IIntegrationEventHandler<DocumentGeneratedV1>
{
    public async Task HandleAsync(DocumentGeneratedV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == integrationEvent.DocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Document {integrationEvent.DocumentId} does not exist.");
        document.MarkGenerated(integrationEvent.StorageKey, integrationEvent.Sha256, integrationEvent.SizeBytes, integrationEvent.ContentType);
    }
}

internal sealed class RecordFailedDocument(DocumentsDbContext db) : IIntegrationEventHandler<DocumentGenerationFailedV1>
{
    public async Task HandleAsync(DocumentGenerationFailedV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == integrationEvent.DocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Document {integrationEvent.DocumentId} does not exist.");
        document.MarkFailed(integrationEvent.Reason);
    }
}
