// Authentication and authorization for the Control Plane API.
//
// Callers present Keycloak tokens from the `platform` realm, issued for the
// `controlplane-api` audience:
// - CI zones use client-credentials tokens of their own client (ci-security-zone,
//   ci-build-zone, ci-trust-zone). The zone comes from a realm role AND must match the
//   client that requested the token, so a human account that was accidentally given a
//   zone role still cannot act as a pipeline.
// - People use their own accounts with roles platform-viewer, risk-owner and
//   security-approver.
// Every endpoint requires a token unless it is explicitly anonymous (deny by default).
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Infrastructure.Storage;

namespace Sscp.ControlPlane.Api.Security;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";
    public string Issuer { get; set; } = string.Empty;
    public string? MetadataAddress { get; set; }
    public string Audience { get; set; } = "controlplane-api";
    public string? CaCertificatePath { get; set; }
}

public static class PlatformRoles
{
    public const string Viewer = "platform-viewer";
    public const string RiskOwner = "risk-owner";
    public const string SecurityApprover = "security-approver";
    public const string Admin = "platform-admin";

    // Zone role → (zone, the only client allowed to carry it).
    public static readonly IReadOnlyDictionary<string, (CallerZone Zone, string Client)> Zones = new Dictionary<string, (CallerZone, string)>
    {
        ["ci-security-zone"] = (CallerZone.Security, "ci-security-zone"),
        ["ci-build-zone"] = (CallerZone.Build, "ci-build-zone"),
        ["ci-trust-zone"] = (CallerZone.Trust, "ci-trust-zone"),
        ["cluster-reporter"] = (CallerZone.Cluster, "cluster-reporter"),
    };

    public static readonly string[] People = [Viewer, RiskOwner, SecurityApprover, Admin];
}

public static class Policies
{
    public const string Pipeline = "pipeline";
    public const string Viewer = "viewer";
    public const string RiskOwner = "risk-owner";
    public const string SecurityApprover = "security-approver";
    public const string PlatformAdmin = "platform-admin";
}

public static class CallerResolver
{
    public const string RoleClaim = "role";

    public static Caller Resolve(ClaimsPrincipal user)
    {
        var roles = user.FindAll(RoleClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var client = user.FindFirst("azp")?.Value ?? user.FindFirst("client_id")?.Value;
        var zones = PlatformRoles.Zones.Where(zone => roles.Contains(zone.Key)).Select(zone => zone.Value).ToList();

        // Exactly one zone role, carried by that zone's own client; anything else is not a zone.
        if (zones.Count == 1 && string.Equals(zones[0].Client, client, StringComparison.Ordinal))
        {
            return new Caller($"zone:{client}", zones[0].Zone);
        }

        var username = user.FindFirst("preferred_username")?.Value ?? user.FindFirst("sub")?.Value ?? "anonymous";
        return new Caller($"user:{username}", CallerZone.None);
    }

    public static bool IsPerson(ClaimsPrincipal user) => Resolve(user).Zone == CallerZone.None;

    public static bool HasRole(ClaimsPrincipal user, params string[] roles) =>
        IsPerson(user) && user.FindAll(RoleClaim).Any(c => roles.Contains(c.Value, StringComparer.Ordinal));
}

public static class ControlPlaneAuth
{
    public static IServiceCollection AddControlPlaneAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var identity = configuration.GetSection(IdentityOptions.SectionName).Get<IdentityOptions>() ?? new IdentityOptions();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = identity.Issuer;
            options.MetadataAddress = identity.MetadataAddress ?? $"{identity.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
            options.Audience = identity.Audience;
            options.MapInboundClaims = false;
            options.BackchannelHttpHandler = LocalCaHttpClientFactory.CreateHandler(identity.CaCertificatePath);
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
                NameClaimType = "preferred_username",
                RoleClaimType = CallerResolver.RoleClaim,
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    FlattenRealmRoles(context.Principal!);
                    return Task.CompletedTask;
                },
            };
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Pipeline, policy => policy.RequireAssertion(context =>
                CallerResolver.Resolve(context.User).Zone is CallerZone.Security or CallerZone.Build or CallerZone.Trust))
            .AddPolicy(Policies.Viewer, policy => policy.RequireAssertion(context => CallerResolver.HasRole(context.User, PlatformRoles.People)))
            .AddPolicy(Policies.RiskOwner, policy => policy.RequireAssertion(context => CallerResolver.HasRole(context.User, PlatformRoles.RiskOwner)))
            .AddPolicy(Policies.SecurityApprover, policy => policy.RequireAssertion(context => CallerResolver.HasRole(context.User, PlatformRoles.SecurityApprover)))
            .AddPolicy(Policies.PlatformAdmin, policy => policy.RequireAssertion(context => CallerResolver.HasRole(context.User, PlatformRoles.Admin)));
        return services;
    }

    // Keycloak puts realm roles in {"realm_access":{"roles":[...]}}; flatten the ones this
    // service knows into role claims and ignore the rest.
    private static void FlattenRealmRoles(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
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

        var known = PlatformRoles.People.Concat(PlatformRoles.Zones.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var role in roles.EnumerateArray().Select(r => r.GetString()).Where(r => r is not null && known.Contains(r)))
        {
            identity.AddClaim(new Claim(CallerResolver.RoleClaim, role!));
        }
    }
}
