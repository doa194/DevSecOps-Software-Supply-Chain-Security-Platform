// Public contract of the Identity module.
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Identity.Contracts;

[IntegrationEvent("identity.user-provisioned", 1)]
public sealed record UserProvisionedV1(string Subject, string Username) : IntegrationEvent;

[IntegrationEvent("identity.user-roles-changed", 1)]
public sealed record UserRolesChangedV1(string Subject, IReadOnlyList<string> AddedRoles, IReadOnlyList<string> RemovedRoles, string ChangedBy) : IntegrationEvent;
