// Verifies and interprets Gitea webhook deliveries.
//
// Gitea signs every delivery with HMAC-SHA256 over the raw body using a secret shared only
// with the Control Plane. An unsigned or wrongly signed delivery is refused before it is
// parsed. Only three events matter: a push to main, a pull request opened or updated
// against main, and a pushed tag; everything else is ignored.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sscp.ControlPlane.Application.Orchestration;

namespace Sscp.ControlPlane.Infrastructure.Gitea;

public static partial class GiteaWebhook
{
    private static readonly string[] PullRequestActions = ["opened", "synchronized", "reopened"];

    public static bool HasValidSignature(ReadOnlySpan<byte> body, string? signatureHex, string secret)
    {
        // No configured secret means nothing can be verified: refuse everything.
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHex) || signatureHex.Length != 64)
        {
            return false;
        }

        byte[] claimed;
        try
        {
            claimed = Convert.FromHexString(signatureHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, claimed);
    }

    public static SourceEvent? Parse(string? eventType, ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var root = document.RootElement;
        var repository = root.TryGetProperty("repository", out var repo) ? repo.GetProperty("full_name").GetString() : null;
        if (repository is null)
        {
            return null;
        }

        return eventType switch
        {
            "push" => ParsePush(root, repository),
            "pull_request" => ParsePullRequest(root, repository),
            _ => null,
        };
    }

    private static SourceEvent? ParsePush(JsonElement root, string repository)
    {
        var gitRef = root.GetProperty("ref").GetString() ?? string.Empty;
        var after = root.TryGetProperty("after", out var a) ? a.GetString() ?? string.Empty : string.Empty;
        var actor = Login(root, "pusher") ?? Login(root, "sender") ?? "unknown";
        if (!Sha().IsMatch(after) || after.All(c => c == '0'))
        {
            return null; // branch or tag deletion
        }

        if (gitRef == "refs/heads/main")
        {
            return new PushedToMain(repository, actor, after);
        }

        return gitRef.StartsWith("refs/tags/", StringComparison.Ordinal)
            ? new TagPushed(repository, actor, gitRef["refs/tags/".Length..])
            : null;
    }

    private static PullRequestUpdated? ParsePullRequest(JsonElement root, string repository)
    {
        var action = root.GetProperty("action").GetString();
        var pullRequest = root.GetProperty("pull_request");
        var baseRef = pullRequest.GetProperty("base").GetProperty("ref").GetString();
        var head = pullRequest.GetProperty("head").GetProperty("sha").GetString() ?? string.Empty;
        if (!PullRequestActions.Contains(action) || baseRef != "main" || !Sha().IsMatch(head))
        {
            return null;
        }

        return new PullRequestUpdated(repository, Login(root, "sender") ?? "unknown", pullRequest.GetProperty("number").GetInt32(), head);
    }

    private static string? Login(JsonElement root, string property) =>
        root.TryGetProperty(property, out var user) && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("login", out var login)
            ? login.GetString()
            : null;

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha();
}
