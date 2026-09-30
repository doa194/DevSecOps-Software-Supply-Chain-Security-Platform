// Minimal Keycloak Admin REST client for realm-role management.
//
// It authenticates as the `commerce-identity-admin` service account using the client
// credentials grant. That account holds only the realm-management roles needed to view
// and manage users of the commerce realm; it cannot change clients, realm settings or
// other realms.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Commerce.BuildingBlocks.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Identity.Data;

public sealed class KeycloakAdminOptions
{
    public const string SectionName = "Identity:Admin";

    // e.g. https://keycloak.sscp.test:9443 (backchannel URL reachable from this service)
    public string BaseUrl { get; set; } = string.Empty;
    public string Realm { get; set; } = "commerce";
    public string ClientId { get; set; } = "commerce-identity-admin";
    public string ClientSecret { get; set; } = string.Empty;
    public string? CaCertificatePath { get; set; }
}

public interface IKeycloakAdmin
{
    // Null when Keycloak has no such user.
    Task<IReadOnlyList<string>?> GetRealmRolesAsync(string userId, CancellationToken cancellationToken);
    Task AddRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken);
    Task RemoveRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken);
}

internal sealed class KeycloakAdminClient(HttpClient http, KeycloakAdminOptions options) : IKeycloakAdmin
{
    public async Task<IReadOnlyList<string>?> GetRealmRolesAsync(string userId, CancellationToken cancellationToken)
    {
        await AuthenticateAsync(cancellationToken);
        using var response = await http.GetAsync($"admin/realms/{options.Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var roles = await response.Content.ReadFromJsonAsync<List<RoleRepresentation>>(cancellationToken);
        return roles?.Select(role => role.Name).ToList() ?? [];
    }

    public Task AddRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken) =>
        ChangeRolesAsync(HttpMethod.Post, userId, roles, cancellationToken);

    public Task RemoveRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken) =>
        ChangeRolesAsync(HttpMethod.Delete, userId, roles, cancellationToken);

    private async Task ChangeRolesAsync(HttpMethod method, string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        if (roles.Count == 0)
        {
            return;
        }

        await AuthenticateAsync(cancellationToken);
        // Role representations come from the user's own mappings (current roles for removal,
        // assignable roles for addition). This works with user-management rights alone; the
        // service account is deliberately not allowed to read realm configuration.
        var source = method == HttpMethod.Delete ? "role-mappings/realm" : "role-mappings/realm/available";
        var candidates = await http.GetFromJsonAsync<List<RoleRepresentation>>(
            $"admin/realms/{options.Realm}/users/{Uri.EscapeDataString(userId)}/{source}", cancellationToken) ?? [];
        var representations = candidates.Where(candidate => roles.Contains(candidate.Name, StringComparer.Ordinal)).ToList();
        if (representations.Count != roles.Count)
        {
            throw new InvalidOperationException($"Keycloak did not offer all requested roles for this user: {string.Join(",", roles)}.");
        }

        using var request = new HttpRequestMessage(method, $"admin/realms/{options.Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm")
        {
            Content = JsonContent.Create(representations),
        };
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            $"realms/{options.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            }),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
    }

    private sealed record RoleRepresentation([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string Name);

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(KeycloakAdminOptions.SectionName).Get<KeycloakAdminOptions>() ?? new KeycloakAdminOptions();
        services.AddSingleton(options);
        services.AddHttpClient<IKeycloakAdmin, KeycloakAdminClient>(client => client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"))
            .ConfigurePrimaryHttpMessageHandler(() => LocalCaTrust.CreateHandler(options.CaCertificatePath));
    }
}
