// Entry point of the Documents module: uploaded attachments and generated invoices.
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Documents.Contracts;
using Commerce.Modules.Documents.Data;
using Commerce.Modules.Documents.Features;
using Commerce.Modules.Documents.Integration;
using Commerce.Modules.Orders.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Documents;

public sealed class DocumentsModule : IModule
{
    public string Name => DocumentsDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<DocumentsDbContext>(configuration, DocumentsDbContext.SchemaName);
        services.AddOutboxPublisher<DocumentsDbContext>();
        services.AddIntegrationEventConsumer("commerce-api.documents", consumer => consumer
            .Handle<OrderConfirmedV1, RequestInvoiceForConfirmedOrder, DocumentsDbContext>()
            .Handle<DocumentGeneratedV1, RecordGeneratedDocument, DocumentsDbContext>()
            .Handle<DocumentGenerationFailedV1, RecordFailedDocument, DocumentsDbContext>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        DocumentEndpoints.Map(endpoints.MapGroup("/api/documents").WithTags("Documents"));
}
