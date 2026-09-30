// Access tokens shaped like Keycloak's `platform` realm tokens (realm_access.roles, azp,
// preferred_username, audience) but signed with a key the tests own, so authorization
// behaviour is deterministic without a running Keycloak.
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Sscp.ControlPlane.ComponentTests.Infrastructure;

public static class TestTokens
{
    public const string Issuer = "https://keycloak.test/realms/platform";
    public static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "component-tests" };

    // Client-credentials token of a CI zone client: the zone role and the client match.
    public static string Zone(string client) => Create($"service-account-{client}", client, [client]);

    // A person using the platform CLI.
    public static string Person(string username, params string[] roles) => Create(username, "sscp-cli", roles);

    public static string Create(string username, string client, string[] roles, string audience = "controlplane-api")
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("preferred_username", username),
            new("azp", client),
            new("realm_access", JsonSerializer.Serialize(new { roles }), JsonClaimValueTypes.Json),
        };
        var token = new JwtSecurityToken(Issuer, audience, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
