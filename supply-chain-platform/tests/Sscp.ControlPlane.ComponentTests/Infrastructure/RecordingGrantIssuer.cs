// Stands in for Vault when the Control Plane mints signing grants. Records which releases
// received a grant and can simulate Vault being unreachable for chosen releases. Real
// Vault behaviour (single use, IP binding, wrapping) is checked by the operational suite.
using System.Collections.Concurrent;
using Sscp.ControlPlane.Application.Releases;

namespace Sscp.ControlPlane.ComponentTests.Infrastructure;

public sealed class RecordingGrantIssuer : ISigningGrantIssuer
{
    public ConcurrentQueue<(Guid Release, long Run)> Issued { get; } = new();
    public ConcurrentDictionary<Guid, bool> Unreachable { get; } = new();

    public Task<SigningGrant> IssueAsync(Guid releaseId, long runId, CancellationToken cancellationToken)
    {
        if (Unreachable.ContainsKey(releaseId))
        {
            throw new SigningUnavailableException("Vault is sealed.");
        }

        Issued.Enqueue((releaseId, runId));
        return Task.FromResult(new SigningGrant("trust-signer-role-id", $"hvs.wrapped-{releaseId:N}", $"accessor-{releaseId:N}", DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}
