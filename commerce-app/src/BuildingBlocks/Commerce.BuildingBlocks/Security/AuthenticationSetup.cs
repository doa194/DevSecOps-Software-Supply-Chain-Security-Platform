// JWT bearer authentication against Keycloak and permission-based authorization.
//
// Rules enforced here for every service:
// - Tokens must be RS256-signed by the configured realm, unexpired, and issued for the
//   `commerce-api` audience.
// - Every endpoint requires an authenticated user unless it explicitly opts out with
//   AllowAnonymous (deny by default).
// - Failed authentication and denied authorization are emitted as security events.
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Commerce.BuildingBlocks.Security;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    // Expected token issuer, e.g. https://keycloak.sscp.test:9443/realms/commerce
    public string Issuer { get; set; } = string.Empty;

    // Where to fetch signing keys. Defaults to the issuer's discovery document; set it when
    // the issuer's host name is not resolvable from where the service runs.
    public string? MetadataAddress { get; set; }

    public string Audience { get; set; } = "commerce-api";
    public string? CaCertificatePath { get; set; }

    // Only integration tests against a throw-away plain-HTTP Keycloak turn this off.
    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddCommerceAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var identity = configuration.GetSection(IdentityOptions.SectionName).Get<IdentityOptions>() ?? new IdentityOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = identity.Issuer;
            options.MetadataAddress = identity.MetadataAddress ?? $"{identity.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
            options.Audience = identity.Audience;
            options.RequireHttpsMetadata = identity.RequireHttpsMetadata;
            options.MapInboundClaims = false;
            options.BackchannelHttpHandler = LocalCaTrust.CreateHandler(identity.CaCertificatePath);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = identity.Issuer,
                ValidAudience = identity.Audience,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = KeycloakClaims.Username,
                RoleClaimType = KeycloakClaims.Role,
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    FlattenRealmRoles(context.Principal!);
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    var events = context.HttpContext.RequestServices.GetRequiredService<SecurityEventLog>();
                    events.AuthenticationFailed(context.Exception.GetType().Name, context.Request.Path, context.HttpContext.Connection.RemoteIpAddress?.ToString());
                    return Task.CompletedTask;
                },
            };
        });

        services.AddCommerceAuthorization();
        return services;
    }

    public static IServiceCollection AddCommerceAuthorization(this IServiceCollection services)
    {
        var builder = services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        foreach (var permission in PermissionMap.AllPermissions)
        {
            builder.AddPolicy(permission, policy => policy.RequireAuthenticatedUser()
                .RequireAssertion(context => PermissionMap.Grants(context.User.FindAll(KeycloakClaims.Role).Select(c => c.Value), permission)));
        }

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();
        return services;
    }

    // Keycloak puts realm roles in {"realm_access":{"roles":[...]}}; turn them into flat claims.
    private static void FlattenRealmRoles(System.Security.Claims.ClaimsPrincipal principal)
    {
        var identity = (System.Security.Claims.ClaimsIdentity)principal.Identity!;
        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (realmAccess is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(realmAccess);
        if (!document.RootElement.TryGetProperty("roles", out var roles))
        {
            return;
        }

        foreach (var role in roles.EnumerateArray().Select(r => r.GetString()).Where(r => r is not null && Roles.All.Contains(r)))
        {
            identity.AddClaim(new System.Security.Claims.Claim(KeycloakClaims.Role, role!));
        }
    }
}

// Emits a security event whenever the authorization middleware denies a request, then lets
// the default handler produce the normal 401/403 response.
internal sealed class AuditingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            var requirement = string.Join(",", authorizeResult.AuthorizationFailure?.FailedRequirements.Select(r => r.ToString()) ?? []);
            context.RequestServices.GetRequiredService<SecurityEventLog>()
                .AuthorizationDenied(context.User.FindFirst(KeycloakClaims.Subject)?.Value, $"{context.Request.Method} {context.Request.Path}", requirement);
        }
        else if (authorizeResult.Challenged && context.Request.Headers.Authorization.Count == 0)
        {
            context.RequestServices.GetRequiredService<SecurityEventLog>()
                .AuthenticationFailed("missing-token", context.Request.Path, context.Connection.RemoteIpAddress?.ToString());
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
