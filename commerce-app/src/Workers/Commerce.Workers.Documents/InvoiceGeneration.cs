// Document processing worker logic: renders an invoice, stores it in object storage and
// reports the result with its SHA-256. It has its own schema holding only its outbox and
// inbox; everything it needs arrives in the request event.
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Storage;
using Commerce.Modules.Documents.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Workers.Documents;

public sealed class DocumentProcessingDbContext(DbContextOptions<DocumentProcessingDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "document_processing";
    public override string Schema => SchemaName;
}

internal sealed class DocumentProcessingDesignTimeFactory() : DesignTimeFactory<DocumentProcessingDbContext>(DocumentProcessingDbContext.SchemaName);

public static class InvoiceRenderer
{
    // Every value is HTML-encoded: product names come from catalog staff input and must
    // not be able to inject markup or script into a document a customer opens.
    public static string Render(DocumentGenerationRequestedV1 request)
    {
        var culture = CultureInfo.InvariantCulture;
        var rows = new StringBuilder();
        foreach (var line in request.Lines)
        {
            rows.Append(culture, $"<tr><td>{Encode(line.Sku)}</td><td>{Encode(line.Name)}</td><td>{line.Quantity}</td><td>{line.UnitPrice:0.00}</td><td>{line.UnitPrice * line.Quantity:0.00}</td></tr>\n");
        }

        return string.Create(culture, $"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>Invoice {request.OrderId:N}</title></head>
            <body>
            <h1>Invoice</h1>
            <p>Order {request.OrderId:D}<br>Customer {request.CustomerId:D}</p>
            <table>
            <tr><th>SKU</th><th>Item</th><th>Qty</th><th>Unit price</th><th>Line total</th></tr>
            {rows}</table>
            <p>Total: {request.Total:0.00} {Encode(request.Currency)}</p>
            </body></html>
            """);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

internal sealed class GenerateInvoice(
    Outbox<DocumentProcessingDbContext> outbox,
    IObjectStorage storage,
    MessagingOptions messaging) : IIntegrationEventHandler<DocumentGenerationRequestedV1>
{
    public async Task HandleAsync(DocumentGenerationRequestedV1 request, MessageContext context, CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes(InvoiceRenderer.Render(request));
        var key = $"invoices/{request.CustomerId:N}/{request.DocumentId:N}.html";
        try
        {
            using var stream = new MemoryStream(content);
            await storage.PutAsync(key, stream, "text/html", cancellationToken);
        }
        catch (Exception error) when (context.Attempt >= messaging.MaxDeliveryAttempts && error is not OperationCanceledException)
        {
            // Out of retries: report the failure so the document does not stay "Requested".
            outbox.Add(new DocumentGenerationFailedV1(request.DocumentId, "storage unavailable"));
            return;
        }

        outbox.Add(new DocumentGeneratedV1(request.DocumentId, key, Convert.ToHexStringLower(SHA256.HashData(content)), content.LongLength, "text/html"));
    }
}
