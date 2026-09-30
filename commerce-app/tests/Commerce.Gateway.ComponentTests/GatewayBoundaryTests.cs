// The gateway is the only public entry point. These tests run the real gateway in front
// of a fake backend that records what reaches it.
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Commerce.Gateway.ComponentTests;

public class GatewayFixture : IAsyncLifetime
{
    // Generous by default, so tests sharing a gateway never trip the limiter by accident.
    protected virtual int AnonymousPerMinute => 1000;

    public const string Issuer = "https://keycloak.test/realms/commerce";
    public static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "gateway-tests" };

    private WebApplication _backend = null!;
    private WebApplicationFactory<Program> _gateway = null!;

    // Every request that reached the backend: path and the X-Forwarded-For it saw.
    public ConcurrentQueue<(string Path, string? ForwardedFor)> BackendRequests { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _backend = WebApplication.CreateBuilder().Build();
        _backend.Urls.Add("http://127.0.0.1:0");
        _backend.Run(async context =>
        {
            BackendRequests.Enqueue((context.Request.Path, context.Request.Headers["X-Forwarded-For"].ToString()));
            await context.Response.WriteAsync("backend");
        });
        await _backend.StartAsync();
        var backendUrl = _backend.Urls.Single();

        _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Identity:Issuer", Issuer);
            builder.UseSetting("RateLimits:AnonymousPerMinute", AnonymousPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var cluster in new[] { "api", "reporting-worker", "audit-worker", "notification-worker" })
            {
                builder.UseSetting($"ReverseProxy:Clusters:{cluster}:Destinations:primary:Address", backendUrl);
            }

            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(Key);
                options.Configuration = configuration;
                options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters.IssuerSigningKey = Key;
            }));
        });
    }

    public HttpClient Client(string? token = null, string? remoteIp = null)
    {
        var client = _gateway.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }

    public static string Token(string username, params string[] roles)
    {
        var claims = new[]
        {
            new Claim("sub", username), new Claim("preferred_username", username),
            new Claim("realm_access", JsonSerializer.Serialize(new { roles }), JsonClaimValueTypes.Json),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, "commerce-api", claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)));
    }

    public async ValueTask DisposeAsync()
    {
        await _gateway.DisposeAsync();
        await _backend.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

// A separate gateway with a small anonymous budget, used only by the rate-limit test.
public sealed class RateLimitedGatewayFixture : GatewayFixture
{
    protected override int AnonymousPerMinute => 5;
}

public sealed class GatewayBoundaryTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task Public_catalog_routes_are_forwarded_without_a_token()
    {
        var response = await gateway.Client().GetAsync("/api/catalog/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(gateway.BackendRequests, r => r.Path == "/api/catalog/products");
    }

    [Theory]
    [InlineData("/api/orders/mine")]
    [InlineData("/api/audit/entries")]
    [InlineData("/api/reports/sales/daily")]
    [InlineData("/api/catalog/products/not-a-guid/secret")]
    public async Task Protected_routes_are_rejected_at_the_gateway_without_reaching_a_backend(string path)
    {
        var response = await gateway.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(gateway.BackendRequests, r => r.Path == path);
    }

    [Fact]
    public async Task Authenticated_requests_are_forwarded()
    {
        var response = await gateway.Client(GatewayFixture.Token("carol", "customer")).GetAsync("/api/customers/me/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_client_supplied_forwarded_for_header_is_replaced()
    {
        var client = gateway.Client(GatewayFixture.Token("carol", "customer"));
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.66.66.66");

        await client.GetAsync("/api/customers/me/forwarded-check");

        var seen = gateway.BackendRequests.Last(r => r.Path == "/api/customers/me/forwarded-check").ForwardedFor;
        Assert.DoesNotContain("10.66.66.66", seen ?? string.Empty, StringComparison.Ordinal);
    }

}

public sealed class GatewayRateLimitTests(RateLimitedGatewayFixture gateway) : IClassFixture<RateLimitedGatewayFixture>
{
    [Fact]
    public async Task Anonymous_callers_are_rate_limited()
    {
        var client = gateway.Client();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 8; i++)
        {
            statuses.Add((await client.GetAsync("/api/catalog/products?page=" + i)).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
