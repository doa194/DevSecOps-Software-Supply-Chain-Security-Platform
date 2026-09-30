// Public contract of the Inventory module.
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Inventory.Contracts;

[IntegrationEvent("inventory.stock-reserved", 1)]
public sealed record StockReservedV1(Guid OrderId) : IntegrationEvent;

[IntegrationEvent("inventory.stock-reservation-failed", 1)]
public sealed record StockReservationFailedV1(Guid OrderId, IReadOnlyList<string> UnavailableSkus) : IntegrationEvent;

[IntegrationEvent("inventory.stock-level-changed", 1)]
public sealed record StockLevelChangedV1(string Sku, int OnHand, int Reserved, int Available) : IntegrationEvent;
