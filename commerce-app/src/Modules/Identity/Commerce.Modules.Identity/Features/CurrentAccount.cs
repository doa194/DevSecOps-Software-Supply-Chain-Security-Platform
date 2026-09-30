// Returns the caller's account and roles, provisioning the local account on first use.
using Commerce.BuildingBlocks.Security;
using Commerce.Modules.Identity.Data;
using Commerce.Modules.Identity.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Identity.Features;

public static class CurrentAccount
{
    public sealed record Response(Guid AccountId, string Subject, string Username, IReadOnlyCollection<string> Roles, IReadOnlyCollection<string> Permissions);

    public static void Map(RouteGroupBuilder identity) =>
        identity.MapGet("/me", HandleAsync).WithName("GetCurrentAccount");

    private static async Task<IResult> HandleAsync(IdentityDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Subject == user.Subject, cancellationToken);
        if (account is null)
        {
            account = UserAccount.Provision(user.Subject!, user.Username ?? user.Subject!, now);
            db.Accounts.Add(account);
        }
        else
        {
            account.Seen(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        var permissions = PermissionMap.AllPermissions.Where(user.HasPermission).Order(StringComparer.Ordinal).ToList();
        return TypedResults.Ok(new Response(account.Id, account.Subject, account.Username, user.Roles, permissions));
    }
}
