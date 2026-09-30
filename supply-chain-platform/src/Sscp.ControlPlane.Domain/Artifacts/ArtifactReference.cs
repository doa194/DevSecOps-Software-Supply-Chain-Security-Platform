// Artifact identity. An artifact is always `repository@sha256:<digest>`; tags are never part
// of identity because anyone with push rights can move a tag. Every trust decision,
// signature and deployment is keyed by this value.
using System.Text.RegularExpressions;
using Sscp.ControlPlane.Domain.Common;

namespace Sscp.ControlPlane.Domain.Artifacts;

public sealed partial record Digest
{
    private Digest(string value) => Value = value;

    // "sha256:" followed by 64 lower-case hex characters.
    public string Value { get; }

    public static Outcome<Digest> Parse(string? value) =>
        value is not null && DigestPattern().IsMatch(value)
            ? new Digest(value)
            : DomainError.Validation("artifact.digest.invalid", "A digest must look like sha256:<64 hex characters>.");

    public override string ToString() => Value;

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestPattern();
}

public sealed partial record ArtifactReference
{
    private ArtifactReference(string repository, Digest digest)
    {
        Repository = repository;
        Digest = digest;
    }

    // Registry host and repository path, e.g. harbor.sscp.test/commerce-candidates/commerce-api
    public string Repository { get; }
    public Digest Digest { get; }

    public static Outcome<ArtifactReference> Create(string? repository, string? digest)
    {
        if (repository is null || !RepositoryPattern().IsMatch(repository))
        {
            return DomainError.Validation("artifact.repository.invalid", "Repository must be registry/project/name without a tag.");
        }

        var parsed = Digest.Parse(digest);
        return parsed.Succeeded ? new ArtifactReference(repository, parsed.Value) : parsed.Error!;
    }

    // Parses "repository@sha256:..." and refuses anything that relies on a tag.
    public static Outcome<ArtifactReference> Parse(string? reference)
    {
        var at = reference?.LastIndexOf('@') ?? -1;
        if (reference is null || at <= 0)
        {
            return DomainError.Validation("artifact.reference.untagged-required", "Artifacts must be referenced by digest (repository@sha256:...).");
        }

        return Create(reference[..at], reference[(at + 1)..]);
    }

    public override string ToString() => $"{Repository}@{Digest}";

    // No tag (":" after the last "/"), lower-case path segments, a host with an optional port.
    [GeneratedRegex(@"^[a-z0-9.-]+(:[0-9]{1,5})?(/[a-z0-9]+([._-][a-z0-9]+)*)+$")]
    private static partial Regex RepositoryPattern();
}
