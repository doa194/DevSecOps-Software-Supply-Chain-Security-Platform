// Administrators view and toggle feature flags. A toggle changes behaviour for every user,
// so it requires a reason and is audited and reported as a privileged operation.
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Administration.Data;
using Commerce.Modules.Administration.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Administration.Features;

public static class FeatureFlagEndpoints
{
    public sealed record FlagDto(string Name, bool Enabled, bool IsDefault);

    public sealed record ToggleRequest(bool Enabled, string Reason);

    public sealed class ToggleValidator : AbstractValidator<ToggleRequest>
    {
        public ToggleValidator() => RuleFor(r => r.Reason).NotEmpty().MaximumLength(200);
    }

    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/features", ListAsync).RequireAuthorization(Permissions.FeaturesManage).WithName("ListFeatureFlags");
        admin.MapPut("/features/{name}", ToggleAsync).RequireAuthorization(Permissions.FeaturesManage).Validate<ToggleRequest>().WithName("ToggleFeatureFlag");
    }

    private static async Task<IResult> ListAsync(DatabaseFeatureDefinitionProvider provider, AdministrationDbContext db, CancellationToken cancellationToken)
    {
        var values = await provider.CurrentValuesAsync(cancellationToken);
        var overridden = await db.FeatureFlags.AsNoTracking().Select(f => f.Id).ToListAsync(cancellationToken);
        return TypedResults.Ok(values.Select(v => new FlagDto(v.Key, v.Value, !overridden.Contains(v.Key))).OrderBy(f => f.Name).ToList());
    }

    private static async Task<IResult> ToggleAsync(
        string name, ToggleRequest request, AdministrationDbContext db, DatabaseFeatureDefinitionProvider provider, ICurrentUser user, SecurityEventLog securityEvents, TimeProvider clock, CancellationToken cancellationToken)
    {
        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Id == name, cancellationToken);
        if (flag is null)
        {
            var created = FeatureFlagOverride.Create(name, request.Enabled, request.Reason, user.Actor, clock.GetUtcNow());
            if (created.IsFailure)
            {
                return created.Error.ToProblem();
            }

            db.FeatureFlags.Add(created.Value);
        }
        else
        {
            flag.Set(request.Enabled, request.Reason, user.Actor, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(cancellationToken);
        provider.Invalidate();
        securityEvents.PrivilegedOperation(user.Actor, $"features.{(request.Enabled ? "enable" : "disable")}", $"feature/{name}");
        return TypedResults.Ok(new FlagDto(name, request.Enabled, false));
    }
}
