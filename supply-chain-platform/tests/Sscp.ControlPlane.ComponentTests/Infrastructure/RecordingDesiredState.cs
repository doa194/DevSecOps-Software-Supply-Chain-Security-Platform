// Stands in for the GitOps repository: tests say which images a revision pins. Reading
// real Kustomize files from Gitea is exercised by the release in the operational checks.
using System.Collections.Concurrent;
using Sscp.ControlPlane.Application;

namespace Sscp.ControlPlane.ComponentTests.Infrastructure;

public sealed class RecordingDesiredState : IDesiredStateReader
{
    public ConcurrentDictionary<string, IReadOnlyList<string>> PinnedByRevision { get; } = new();

    public Task<IReadOnlyList<string>?> PinnedImagesAsync(GitOpsTarget target, string revision, CancellationToken cancellationToken) =>
        Task.FromResult(PinnedByRevision.TryGetValue(revision, out var images) ? images : null);
}
