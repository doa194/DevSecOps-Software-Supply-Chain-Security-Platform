// A rewrite of the audit log made with privileges above the Control Plane's (for example
// by a database administrator) is detected by re-computing the hash chain. Runs on its own
// database because it deliberately breaks the chain.
using Microsoft.EntityFrameworkCore;
using Sscp.ControlPlane.Domain.Auditing;
using Sscp.ControlPlane.Infrastructure.Persistence;
using Sscp.ControlPlane.Infrastructure.Queries;

namespace Sscp.ControlPlane.IntegrationTests;

public sealed class AuditTamperDetectionTests(ControlPlaneDatabase database) : IClassFixture<ControlPlaneDatabase>
{
    [Fact]
    public async Task Rewriting_an_audit_entry_breaks_the_chain_at_that_entry()
    {
        await using (var db = database.RuntimeContext())
        {
            var store = new EfControlPlaneStore(db, TimeProvider.System);
            store.Audit("user:sean", "exception.approved", "exception", "e-1", new { note = "accepted" });
            store.Audit("user:sean", "exception.approved", "exception", "e-2", new { note = "accepted" });
            await store.SaveChangesAsync(CancellationToken.None);
        }

        long target;
        await using (var db = database.RuntimeContext())
        {
            target = await db.AuditLog.Where(a => a.SubjectId == "e-1").Select(a => a.Sequence).SingleAsync();
        }

        // Only the database owner can do this; the chain still shows it.
        await ControlPlaneDatabase.ExecuteAsync(database.OwnerConnection,
            $"UPDATE trust.audit_log SET actor = 'user:rita' WHERE sequence = {target}");

        await using var reader = database.RuntimeContext();
        AuditChain.VerificationResult result = await new ControlPlaneQueries(reader, TimeProvider.System).VerifyAuditChainAsync(CancellationToken.None);
        Assert.False(result.Intact);
        Assert.Equal(target, result.FirstBrokenSequence);
    }
}
