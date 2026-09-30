// Document processing worker: generates invoice documents asynchronously. It has no
// public API; only health endpoints are exposed.
using Commerce.BuildingBlocks.Health;
using Commerce.BuildingBlocks.Hosting;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Storage;
using Commerce.Modules.Documents.Contracts;
using Commerce.Workers.Documents;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerceServiceDefaults("document-worker");
builder.Services.AddCommerceMessaging(builder.Configuration, typeof(DocumentGenerationRequestedV1).Assembly);
builder.Services.AddModuleDbContext<DocumentProcessingDbContext>(builder.Configuration, DocumentProcessingDbContext.SchemaName);
builder.Services.AddOutboxPublisher<DocumentProcessingDbContext>();
builder.Services.AddObjectStorage(builder.Configuration);
builder.Services.AddIntegrationEventConsumer("document-worker.generation", consumer => consumer
    .Handle<DocumentGenerationRequestedV1, GenerateInvoice, DocumentProcessingDbContext>());
builder.Services.AddHealthChecks().AddRabbitMqReadiness();

var app = builder.Build();

if (args.Contains("migrate"))
{
    await MigrationRunner.MigrateAsync(app.Services, app.Configuration,
        [new(typeof(DocumentProcessingDbContext), DocumentProcessingDbContext.SchemaName, "commerce_document_worker")], app.Logger, CancellationToken.None);
    return;
}

app.UseCommerceServiceDefaults();
await app.RunAsync();
