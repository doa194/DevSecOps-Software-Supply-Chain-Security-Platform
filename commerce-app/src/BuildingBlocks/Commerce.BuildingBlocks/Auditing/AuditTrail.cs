// Records business and security audit entries through the module's outbox, so the audit
// record commits (or rolls back) together with the change it describes.
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Security;
using Commerce.SharedKernel.Auditing;

namespace Commerce.BuildingBlocks.Auditing;

public sealed class AuditTrail<TContext>(Outbox<TContext> outbox, ICurrentUser user, ICorrelationContext correlation)
    where TContext : ModuleDbContext
{
    public void Record(
        string action,
        string resourceType,
        string resourceId,
        string category = AuditCategories.Business,
        string outcome = AuditOutcomes.Succeeded,
        IReadOnlyDictionary<string, string>? details = null,
        string? actor = null)
    {
        outbox.Add(new AuditRecordedV1
        {
            Actor = actor ?? user.Actor,
            Action = action,
            Category = category,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = outcome,
            Details = details ?? new Dictionary<string, string>(),
            CorrelationId = correlation.CorrelationId,
        });
    }
}
