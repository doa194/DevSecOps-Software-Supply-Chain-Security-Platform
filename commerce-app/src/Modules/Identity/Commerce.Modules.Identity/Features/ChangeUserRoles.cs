// Administrators replace a user's realm roles. This is a privileged operation:
// - self-changes are refused and reported as a policy violation,
// - every change produces a security event, an audit record and an integration event,
// - if recording the change fails, the Keycloak change is reverted so a role change can
//   never exist without its audit record.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Identity.Contracts;
using Commerce.Modules.Identity.Data;
using Commerce.Modules.Identity.Domain;
using Commerce.SharedKernel.Auditing;
using Commerce.SharedKernel.Results;
using FluentValidation;
using SecurityRoles = Commerce.BuildingBlocks.Security.Roles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Commerce.Modules.Identity.Features;

public static class ChangeUserRoles
{
    public sealed record Request(IReadOnlyList<string> Roles);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => RuleFor(r => r.Roles).NotNull().Must(roles => roles.Count <= SecurityRoles.All.Count);
    }

    public static void Map(RouteGroupBuilder identity) =>
        // Keycloak user ids are UUIDs; anything else cannot name a user.
        identity.MapPut("/users/{subject:guid}/roles", HandleAsync)
            .RequireAuthorization(Permissions.RolesManage)
            .Validate<Request>()
            .WithName("ChangeUserRoles");

    private static async Task<IResult> HandleAsync(
        Guid subject,
        Request request,
        IKeycloakAdmin keycloak,
        IdentityDbContext db,
        Outbox<IdentityDbContext> outbox,
        AuditTrail<IdentityDbContext> audit,
        ICurrentUser user,
        SecurityEventLog securityEvents,
        CancellationToken cancellationToken)
    {
        var target = subject.ToString();
        // Self-changes are refused before Keycloak is even asked for the current roles.
        var current = string.Equals(user.Subject, target, StringComparison.Ordinal)
            ? []
            : await keycloak.GetRealmRolesAsync(target, cancellationToken);
        if (current is null)
        {
            return Error.NotFound("identity.user.unknown", "No such user.").ToProblem();
        }

        var plan = RoleChangePolicy.Evaluate(user.Subject!, target, SecurityRoles.All, current, request.Roles);
        if (plan.IsFailure)
        {
            if (plan.Error.Type == ErrorType.Forbidden)
            {
                securityEvents.PolicyViolation(user.Subject, "identity.no-self-role-change", $"attempted to change own roles to [{string.Join(",", request.Roles)}]");
                audit.Record("identity.roles.change", "user", target, AuditCategories.Security, AuditOutcomes.Denied);
                await db.SaveChangesAsync(cancellationToken);
            }

            return plan.Error.ToProblem();
        }

        var (add, remove) = (plan.Value.Add, plan.Value.Remove);
        await keycloak.AddRealmRolesAsync(target, add, cancellationToken);
        await keycloak.RemoveRealmRolesAsync(target, remove, cancellationToken);

        try
        {
            outbox.Add(new UserRolesChangedV1(target, add, remove, user.Actor));
            audit.Record("identity.roles.change", "user", target, AuditCategories.Security, details: new Dictionary<string, string>
            {
                ["added"] = string.Join(",", add),
                ["removed"] = string.Join(",", remove),
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Undo the change in Keycloak: an unaudited role change must not survive.
            await keycloak.RemoveRealmRolesAsync(target, add, CancellationToken.None);
            await keycloak.AddRealmRolesAsync(target, remove, CancellationToken.None);
            throw;
        }

        securityEvents.RoleChanged(user.Actor, target, $"+[{string.Join(",", add)}] -[{string.Join(",", remove)}]");
        return TypedResults.Ok(new { subject = target, added = add, removed = remove });
    }
}
