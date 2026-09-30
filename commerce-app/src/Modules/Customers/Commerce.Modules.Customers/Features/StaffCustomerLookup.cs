// Staff access to customer records (support agents, order managers, administrators).
// Personal data is masked unless the caller has personal-data clearance, and every read
// of another person's record is written to the audit trail.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Customers.Data;
using Commerce.SharedKernel.Auditing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Customers.Features;

public static class StaffCustomerLookup
{
    public static void Map(RouteGroupBuilder customers)
    {
        customers.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Permissions.CustomersReadAny).WithName("GetCustomer");
        customers.MapGet("/", SearchAsync).RequireAuthorization(Permissions.CustomersReadAny).WithName("SearchCustomers");
    }

    private static async Task<IResult> GetAsync(
        Guid id, CustomersDbContext db, AuditTrail<CustomersDbContext> audit, ICurrentUser user, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        var unmasked = user.HasPermission(Permissions.PersonalDataRead);
        audit.Record("customers.record.view", "customer", id.ToString(), AuditCategories.Security,
            details: new Dictionary<string, string> { ["unmasked"] = unmasked.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        if (unmasked)
        {
            securityEvents.PrivilegedOperation(user.Actor, "customers.view-unmasked", $"customer/{id}");
        }

        return TypedResults.Ok(CustomerDto.From(customer));
    }

    private static async Task<IResult> SearchAsync(string? email, [AsParameters] PageRequest paging, CustomersDbContext db, CancellationToken cancellationToken)
    {
        var query = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalised = email.Trim().ToLowerInvariant();
            query = query.Where(c => c.Email == normalised);
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderBy(c => c.RegisteredAt).Skip(paging.Skip).Take(paging.SafePageSize).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<CustomerDto>(page.Select(CustomerDto.From).ToList(), paging.SafePage, paging.SafePageSize, total));
    }
}
