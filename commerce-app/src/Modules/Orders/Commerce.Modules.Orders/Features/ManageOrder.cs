// Commands on an existing order: cancel (owner or order manager) and fulfil (order manager).
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Administration.Contracts;
using Commerce.Modules.Orders.Data;
using Commerce.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;

namespace Commerce.Modules.Orders.Features;

public static class ManageOrder
{
    public static void Map(RouteGroupBuilder orders)
    {
        orders.MapPost("/{id:guid}/cancel", CancelAsync).WithName("CancelOrder");
        orders.MapPost("/{id:guid}/fulfil", FulfilAsync).RequireAuthorization(Permissions.OrdersManage).WithName("FulfilOrder");
    }

    private static async Task<IResult> CancelAsync(
        Guid id, OrdersDbContext db, ICurrentUser user, IFeatureManager features, SecurityEventLog securityEvents, TimeProvider clock, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return TypedResults.NotFound();
        }

        var isOwner = OrderAccess.IsOwner(order, user);
        var isStaff = user.HasPermission(Permissions.OrdersManage);
        if (!isOwner && !isStaff)
        {
            OrderAccess.ReportDenied(securityEvents, user, id, "owner-or-orders:manage");
            return TypedResults.NotFound();
        }

        var ownerMayCancelAfterPayment = await features.IsEnabledAsync(FeatureFlags.OrdersCancelAfterPayment, cancellationToken);
        var result = order.Cancel(user.Actor, isOwner, isStaff, ownerMayCancelAfterPayment, clock.GetUtcNow());
        if (result.IsFailure)
        {
            if (result.Error.Type == ErrorType.Forbidden)
            {
                securityEvents.AuthorizationDenied(user.Subject, $"order/{id}", "cancel-at-this-stage");
            }

            return result.Error.ToProblem();
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(OrderDto.From(order));
    }

    private static async Task<IResult> FulfilAsync(Guid id, OrdersDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return TypedResults.NotFound();
        }

        var result = order.Fulfil(user.Actor, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(OrderDto.From(order));
    }
}
