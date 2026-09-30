// Queries (the read side of the Orders module). Reads bypass the aggregate's behaviour and
// change tracking; they apply resource-based access rules and return read models only.
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Orders.Data;
using Commerce.Modules.Orders.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Orders.Features;

public static class OrderQueries
{
    public static void Map(RouteGroupBuilder orders)
    {
        orders.MapGet("/{id:guid}", GetAsync).WithName("GetOrder");
        orders.MapGet("/mine", MineAsync).RequireAuthorization(Permissions.OrdersPlace).WithName("GetMyOrders");
        orders.MapGet("/", SearchAsync).RequireAuthorization(Permissions.OrdersReadAny).WithName("SearchOrders");
    }

    private static async Task<IResult> GetAsync(Guid id, OrdersDbContext db, ICurrentUser user, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return TypedResults.NotFound();
        }

        if (!OrderAccess.CanView(order, user))
        {
            OrderAccess.ReportDenied(securityEvents, user, id, "owner-or-orders:read-any");
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(OrderDto.From(order));
    }

    private static async Task<IResult> MineAsync([AsParameters] PageRequest paging, OrdersDbContext db, ICurrentUser user, CancellationToken cancellationToken)
    {
        var query = db.Orders.AsNoTracking().Where(o => o.OwnerSubject == user.Subject);
        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderByDescending(o => o.PlacedAt).Skip(paging.Skip).Take(paging.SafePageSize).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<OrderDto>(page.Select(OrderDto.From).ToList(), paging.SafePage, paging.SafePageSize, total));
    }

    private static async Task<IResult> SearchAsync(OrderStatus? status, [AsParameters] PageRequest paging, OrdersDbContext db, CancellationToken cancellationToken)
    {
        var query = db.Orders.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(o => o.Status == wanted);
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderByDescending(o => o.PlacedAt).Skip(paging.Skip).Take(paging.SafePageSize).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<OrderDto>(page.Select(OrderDto.From).ToList(), paging.SafePage, paging.SafePageSize, total));
    }
}
