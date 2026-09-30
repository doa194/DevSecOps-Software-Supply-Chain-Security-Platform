// Role and permission model.
//
// Keycloak issues realm roles (who someone is); the application checks permissions (what
// an operation needs). This table is the single place that decides which roles grant
// which permission, so changing a job description never means hunting through endpoints.
// Resource-based rules (for example "a customer may read only their own order") are
// evaluated on top of these permissions by each module.
namespace Commerce.BuildingBlocks.Security;

public static class Roles
{
    public const string Customer = "customer";
    public const string SupportAgent = "support-agent";
    public const string CatalogManager = "catalog-manager";
    public const string InventoryClerk = "inventory-clerk";
    public const string OrderManager = "order-manager";
    public const string Finance = "finance";
    public const string Auditor = "auditor";
    public const string Admin = "admin";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Customer, SupportAgent, CatalogManager, InventoryClerk, OrderManager, Finance, Auditor, Admin,
    };
}

public static class Permissions
{
    public const string CatalogWrite = "catalog:write";
    public const string InventoryRead = "inventory:read";
    public const string InventoryWrite = "inventory:write";
    public const string OrdersPlace = "orders:place";
    public const string OrdersReadAny = "orders:read-any";
    public const string OrdersManage = "orders:manage";
    public const string CustomersReadAny = "customers:read-any";
    public const string PaymentsReadAny = "payments:read-any";
    public const string PaymentsRefund = "payments:refund";
    public const string DocumentsReadAny = "documents:read-any";
    public const string ReportsRead = "reports:read";
    public const string AuditRead = "audit:read";
    public const string FeaturesManage = "features:manage";
    public const string RolesManage = "roles:manage";

    // Clearance to see classified data unmasked in other people's records.
    public const string PersonalDataRead = "data:personal:read";
    public const string FinancialDataRead = "data:financial:read";
}

public static class PermissionMap
{
    private static readonly Dictionary<string, string[]> RolesByPermission = new(StringComparer.Ordinal)
    {
        [Permissions.CatalogWrite] = [Roles.CatalogManager, Roles.Admin],
        [Permissions.InventoryRead] = [Roles.InventoryClerk, Roles.OrderManager, Roles.Admin],
        [Permissions.InventoryWrite] = [Roles.InventoryClerk, Roles.Admin],
        [Permissions.OrdersPlace] = [Roles.Customer],
        [Permissions.OrdersReadAny] = [Roles.OrderManager, Roles.SupportAgent, Roles.Admin],
        [Permissions.OrdersManage] = [Roles.OrderManager, Roles.Admin],
        [Permissions.CustomersReadAny] = [Roles.SupportAgent, Roles.OrderManager, Roles.Admin],
        [Permissions.PaymentsReadAny] = [Roles.Finance, Roles.Admin],
        // Refunds move money: only finance, and deliberately not admin (separation of duties).
        [Permissions.PaymentsRefund] = [Roles.Finance],
        [Permissions.DocumentsReadAny] = [Roles.SupportAgent, Roles.Admin],
        [Permissions.ReportsRead] = [Roles.Finance, Roles.OrderManager, Roles.Admin],
        [Permissions.AuditRead] = [Roles.Auditor, Roles.Admin],
        [Permissions.FeaturesManage] = [Roles.Admin],
        [Permissions.RolesManage] = [Roles.Admin],
        [Permissions.PersonalDataRead] = [Roles.Admin],
        [Permissions.FinancialDataRead] = [Roles.Finance, Roles.Admin],
    };

    public static IEnumerable<string> AllPermissions => RolesByPermission.Keys;

    public static bool Grants(IEnumerable<string> roles, string permission) =>
        RolesByPermission.TryGetValue(permission, out var allowed) && roles.Any(role => allowed.Contains(role, StringComparer.Ordinal));
}
