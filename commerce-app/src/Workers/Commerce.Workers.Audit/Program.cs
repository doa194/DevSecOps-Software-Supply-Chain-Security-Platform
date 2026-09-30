// Audit worker: sole owner of the audit trail. It consumes audit records published by
// every module, appends them to the hash chain and serves a read-only query API to
// auditors (routed through the gateway at /api/audit).
using Commerce.BuildingBlocks.Health;
using Commerce.BuildingBlocks.Hosting;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.SharedKernel.Auditing;
using Commerce.Workers.Audit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerceServiceDefaults("audit-worker");
builder.Services.AddCommerceMessaging(builder.Configuration);
builder.Services.AddModuleDbContext<AuditDbContext>(builder.Configuration, AuditDbContext.SchemaName);
builder.Services.AddIntegrationEventConsumer("audit-worker.audit", consumer => consumer
    .Handle<AuditRecordedV1, AppendAuditEntry, AuditDbContext>());
builder.Services.AddHealthChecks().AddRabbitMqReadiness();

var app = builder.Build();

if (args.Contains("migrate"))
{
    // Append-only: the runtime role gets SELECT and INSERT, never UPDATE or DELETE.
    await MigrationRunner.MigrateAsync(app.Services, app.Configuration,
        [new(typeof(AuditDbContext), AuditDbContext.SchemaName, "commerce_audit_worker", AppendOnly: true)],
        app.Logger, CancellationToken.None);
    return;
}

app.UseCommerceServiceDefaults();

var audit = app.MapGroup("/api/audit").RequireAuthorization(Permissions.AuditRead).WithTags("Audit");

audit.MapGet("/entries", async (string? actor, string? resourceType, string? resourceId, string? action,
    DateTimeOffset? from, DateTimeOffset? to, [AsParameters] PageRequest paging, AuditDbContext db, CancellationToken cancellationToken) =>
{
    var query = db.Entries.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(actor)) { query = query.Where(e => e.Actor == actor); }
    if (!string.IsNullOrWhiteSpace(resourceType)) { query = query.Where(e => e.ResourceType == resourceType); }
    if (!string.IsNullOrWhiteSpace(resourceId)) { query = query.Where(e => e.ResourceId == resourceId); }
    if (!string.IsNullOrWhiteSpace(action)) { query = query.Where(e => e.Action == action); }
    if (from is { } start) { query = query.Where(e => e.OccurredAt >= start); }
    if (to is { } end) { query = query.Where(e => e.OccurredAt <= end); }

    var total = await query.CountAsync(cancellationToken);
    var page = await query.OrderByDescending(e => e.Sequence).Skip(paging.Skip).Take(paging.SafePageSize).ToListAsync(cancellationToken);
    return TypedResults.Ok(new PagedResult<AuditEntry>(page, paging.SafePage, paging.SafePageSize, total));
}).WithName("SearchAuditEntries");

audit.MapGet("/verify", async (AuditDbContext db, CancellationToken cancellationToken) =>
{
    var entries = db.Entries.AsNoTracking().OrderBy(e => e.Sequence).AsAsyncEnumerable();
    var ordered = new List<AuditEntry>();
    await foreach (var entry in entries.WithCancellation(cancellationToken))
    {
        ordered.Add(entry);
    }

    return TypedResults.Ok(AuditHashChain.Verify(ordered));
}).WithName("VerifyAuditChain");

await app.RunAsync();
