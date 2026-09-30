// Access tokens for component tests, shaped exactly like Keycloak's (realm_access.roles,
// sub, preferred_username, email, audience) but signed with a key the test owns. This
// makes authorization tests deterministic and fast; real Keycloak tokens are covered by
// the integration tests.
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Commerce.ComponentTests.Infrastructure;

public static class TestTokens
{
    public const string Issuer = "https://keycloak.test/realms/commerce";
    public static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "component-tests" };

    public static string For(string username, params string[] roles) =>
        Create(username, roles, "commerce-api", SigningKey);

    public static string Create(string username, string[] roles, string audience, SecurityKey key)
    {
        var claims = new List<Claim>
        {
            new("sub", Subject(username)),
            new("preferred_username", username),
            new("email", $"{username}@commerce.test"),
            new("realm_access", JsonSerializer.Serialize(new { roles }), JsonClaimValueTypes.Json),
        };
        var token = new JwtSecurityToken(Issuer, audience, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // Stable fake Keycloak subject per username.
    public static string Subject(string username) =>
        new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(username)).AsSpan(0, 16)).ToString();
}
