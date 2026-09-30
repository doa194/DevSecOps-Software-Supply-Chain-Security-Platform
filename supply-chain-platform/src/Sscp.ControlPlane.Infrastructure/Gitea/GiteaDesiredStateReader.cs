// Reads the images a GitOps revision pins, straight from the GitOps repository in Gitea.
//
// The Control Plane uses this to check a deployment report against Git itself: the
// Kustomize `images` of the reported revision must be exactly the digests the Control
// Plane approved for the release. The revision is a commit id, so the content read here
// is the content Argo CD deployed.
using System.Net;
using YamlDotNet.Serialization;
using Sscp.ControlPlane.Application;

namespace Sscp.ControlPlane.Infrastructure.Gitea;

public sealed class GiteaDesiredStateReader(HttpClient http) : IDesiredStateReader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public async Task<IReadOnlyList<string>?> PinnedImagesAsync(GitOpsTarget target, string revision, CancellationToken cancellationToken)
    {
        var path = $"{target.Path.Trim('/')}/kustomization.yaml";
        using var response = await http.GetAsync($"api/v1/repos/{target.Repository}/raw/{path}?ref={Uri.EscapeDataString(revision)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var document = Yaml.Deserialize<KustomizationDocument?>(await response.Content.ReadAsStringAsync(cancellationToken));
        return (document?.Images ?? [])
            .Where(image => !string.IsNullOrEmpty(image.Digest))
            .Select(image => $"{(string.IsNullOrEmpty(image.NewName) ? image.Name : image.NewName)}@{image.Digest}")
            .ToList();
    }

    private sealed class KustomizationDocument
    {
        [YamlMember(Alias = "images")]
        public List<ImageDocument>? Images { get; set; }
    }

    private sealed class ImageDocument
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; } = string.Empty;

        [YamlMember(Alias = "newName")]
        public string? NewName { get; set; }

        [YamlMember(Alias = "digest")]
        public string? Digest { get; set; }
    }
}
