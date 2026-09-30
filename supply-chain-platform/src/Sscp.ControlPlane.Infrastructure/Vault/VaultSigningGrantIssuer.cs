// Mints signing grants in Vault as the Control Plane's own AppRole.
//
// That AppRole may only create secret_ids for the `trust-signer` role and read its
// role_id; it cannot sign. Each secret_id is requested response-wrapped: the Control Plane
// only ever sees the wrapping token, which the trust zone unwraps exactly once. The
// secret_id itself is single-use, short-lived and bound to the trust runner's address
// (configured on the role), and its metadata names the release and run for Vault's audit log.
using System.Net.Http.Json;
using System.Text.Json;
using Sscp.ControlPlane.Application.Releases;

namespace Sscp.ControlPlane.Infrastructure.Vault;

public sealed class VaultOptions
{
    public const string SectionName = "Vault";
    public string Address { get; set; } = "https://vault.sscp.test:8200";
    public string RoleId { get; set; } = string.Empty;
    public string SecretId { get; set; } = string.Empty;
    public string SignerRole { get; set; } = "trust-signer";
    public int GrantTtlSeconds { get; set; } = 300;
    public string? CaCertificatePath { get; set; }
}

public sealed class VaultSigningGrantIssuer(HttpClient http, VaultOptions options, TimeProvider clock) : ISigningGrantIssuer
{
    public async Task<SigningGrant> IssueAsync(Guid releaseId, long runId, CancellationToken cancellationToken)
    {
        try
        {
            var token = await LoginAsync(cancellationToken);
            using var roleRequest = Request(HttpMethod.Get, $"v1/auth/approle/role/{options.SignerRole}/role-id", token);
            var roleId = (await SendAsync(roleRequest, cancellationToken)).GetProperty("data").GetProperty("role_id").GetString()!;

            using var grantRequest = Request(HttpMethod.Post, $"v1/auth/approle/role/{options.SignerRole}/secret-id", token);
            grantRequest.Headers.Add("X-Vault-Wrap-TTL", $"{options.GrantTtlSeconds}s");
            // Vault accepts metadata only as a JSON-encoded object of string values.
            var metadata = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["release"] = releaseId.ToString(), ["run"] = runId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            grantRequest.Content = JsonContent.Create(new { metadata });
            var wrap = (await SendAsync(grantRequest, cancellationToken)).GetProperty("wrap_info");
            return new SigningGrant(roleId, wrap.GetProperty("token").GetString()!, wrap.GetProperty("accessor").GetString()!,
                clock.GetUtcNow().AddSeconds(wrap.GetProperty("ttl").GetInt32()));
        }
        catch (HttpRequestException error)
        {
            throw new SigningUnavailableException($"Vault could not issue a signing grant: {error.Message}", error);
        }
    }

    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        using var login = new HttpRequestMessage(HttpMethod.Post, "v1/auth/approle/login")
        {
            Content = JsonContent.Create(new { role_id = options.RoleId, secret_id = options.SecretId }),
        };
        return (await SendAsync(login, cancellationToken)).GetProperty("auth").GetProperty("client_token").GetString()!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Vault-Token", token);
        return request;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Vault's error texts describe the problem and never contain secrets.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Vault answered {(int)response.StatusCode} for {request.Method} {request.RequestUri}: {detail[..Math.Min(detail.Length, 300)]}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }
}
