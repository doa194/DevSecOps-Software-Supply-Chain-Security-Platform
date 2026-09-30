// The Control Plane's history must survive a compromised Control Plane process: its
// database role cannot delete anything or rewrite evidence and audit records, and any
// rewrite made with higher privileges is detected by re-computing the audit hash chain.
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sscp.ControlPlane.Infrastructure.Persistence;
using Sscp.ControlPlane.Infrastructure.Queries;

namespace Sscp.ControlPlane.IntegrationTests;

public sealed class HistoryProtectionTests(ControlPlaneDatabase database) : IClassFixture<ControlPlaneDatabase>
{
    [Theory]
    [InlineData("DELETE FROM trust.audit_log")]
    [InlineData("UPDATE trust.audit_log SET details = '{}'")]
    [InlineData("DELETE FROM trust.evidence")]
    [InlineData("UPDATE trust.evidence SET report_sha256 = 'x'")]
    [InlineData("UPDATE trust.trust_decisions SET outcome = 'Pass'")]
    [InlineData("DELETE FROM trust.artifacts")]
    [InlineData("TRUNCATE trust.audit_log")]
    public async Task Runtime_role_cannot_delete_or_rewrite_history(string statement)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => ControlPlaneDatabase.ExecuteAsync(database.RuntimeConnection, statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Concurrent_writers_produce_one_unbroken_audit_chain()
    {
        // Twenty independent requests, each with its own store and transaction.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            await using var db = database.RuntimeContext();
            var store = new EfControlPlaneStore(db, TimeProvider.System);
            store.Audit("zone:ci-security-zone", "test.concurrent", "test", $"writer-{i}", new { i });
            await store.SaveChangesAsync(CancellationToken.None);
        }));

        await using var reader = database.RuntimeContext();
        var result = await new ControlPlaneQueries(reader, TimeProvider.System).VerifyAuditChainAsync(CancellationToken.None);
        Assert.True(result.Intact);
        Assert.Equal(20, await reader.AuditLog.CountAsync(a => a.Action == "test.concurrent"));
    }
}
