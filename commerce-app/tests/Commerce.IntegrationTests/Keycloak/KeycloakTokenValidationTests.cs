// The shared JWT setup against a real Keycloak: tokens issued for the commerce-api
// audience are accepted and their realm roles become permissions; tokens issued for any
// other audience, altered tokens and missing tokens are refused.
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Commerce.BuildingBlocks.Security;
using Commerce.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Keycloak;

namespace Commerce.IntegrationTests.Keycloak;

public sealed class KeycloakFixture : IAsyncLifetime
{
    // Keycloak imports a file only if it is named <realm>-realm.json.
    private readonly KeycloakContainer _keycloak = new KeycloakBuilder(TestImages.Keycloak)
        .WithResourceMapping(Path.Combine(AppContext.BaseDirectory, "Keycloak", "commerce-test-realm.json"), "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    private WebApplication _app = null!;

    public HttpClient Api { get; private set; } = null!;
    public string RealmUrl => $"{_keycloak.GetBaseAddress().TrimEnd('/')}/realms/commerce-test";

    public async ValueTask InitializeAsync()
    {
        await _keycloak.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Identity:Issuer"] = RealmUrl;
        builder.Configuration["Identity:RequireHttpsMetadata"] = "false";
        builder.Services.AddSingleton<SecurityEventLog>();
        builder.Services.AddCommerceAuthentication(builder.Configuration);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGet("/refunds", () => TypedResults.Ok()).RequireAuthorization(Permissions.PaymentsRefund);
        _app.MapGet("/catalog", () => TypedResults.Ok()).RequireAuthorization(Permissions.CatalogWrite);
        await _app.StartAsync();
        Api = _app.GetTestClient();
    }

    public async Task<string> TokenAsync(string clientId)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync($"{RealmUrl}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = "fiona",
            ["password"] = "integration-test-only",
            ["scope"] = "openid",
        }));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        await _app.DisposeAsync();
        await _keycloak.DisposeAsync();
    }
}

public sealed class KeycloakTokenValidationTests(KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    [Fact]
    public async Task A_keycloak_token_for_the_api_audience_grants_the_users_permissions()
    {
        var token = await keycloak.TokenAsync("commerce-cli");

        var allowed = await SendAsync("/refunds", token);
        var forbidden = await SendAsync("/catalog", token);

        Assert.Equal(HttpStatusCode.OK, allowed);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
    }

    [Fact]
    public async Task A_token_issued_for_another_audience_is_rejected()
    {
        var token = await keycloak.TokenAsync("other-cli");

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync("/refunds", token));
    }

    [Fact]
    public async Task A_tampered_token_is_rejected()
    {
        var token = await keycloak.TokenAsync("commerce-cli");
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(Base64UrlDecode(parts[1]))!;
        payload["realm_access"] = new { roles = new[] { "admin", "finance" } };
        var forged = $"{parts[0]}.{Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload))}.{parts[2]}";

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync("/refunds", forged));
    }

    [Fact]
    public async Task A_request_without_a_token_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync("/refunds", null));

    private async Task<HttpStatusCode> SendAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await keycloak.Api.SendAsync(request);
        return response.StatusCode;
    }

    private static byte[] Base64UrlDecode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '='));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
