// The caller of the current request, read from the validated access token.
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Commerce.BuildingBlocks.Security;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    // Keycloak subject (stable user id). Null outside an authenticated request, for
    // example while handling a message.
    string? Subject { get; }

    string? Username { get; }
    IReadOnlyCollection<string> Roles { get; }
    bool HasPermission(string permission);

    // Audit actor: the subject, or "service:<name>" for automated work.
    string Actor { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor, ServiceIdentity service) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public string? Subject => IsAuthenticated ? Principal!.FindFirstValue(KeycloakClaims.Subject) : null;
    public string? Username => IsAuthenticated ? Principal!.FindFirstValue(KeycloakClaims.Username) : null;

    public IReadOnlyCollection<string> Roles =>
        IsAuthenticated ? Principal!.FindAll(KeycloakClaims.Role).Select(claim => claim.Value).ToArray() : [];

    public bool HasPermission(string permission) => PermissionMap.Grants(Roles, permission);

    public string Actor => Subject ?? $"service:{service.Name}";
}

// Name of the running deployable, used as the audit actor for automated actions.
public sealed record ServiceIdentity(string Name);

public static class KeycloakClaims
{
    public const string Subject = "sub";
    public const string Username = "preferred_username";

    // Keycloak nests realm roles in a JSON claim; they are flattened into this claim type.
    public const string Role = "role";
}
