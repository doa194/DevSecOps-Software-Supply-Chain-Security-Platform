// Turns order and payment events into customer notifications.
//
// Delivery goes through INotificationChannel. The local channel writes a structured log
// line instead of sending e-mail (the platform has no mail server); because the recipient
// address is classified as personal data, the log contains its HMAC token, not the address.
using Commerce.BuildingBlocks.Messaging;
using Commerce.Modules.Customers.Contracts;
using Commerce.Modules.Orders.Contracts;
using Commerce.Modules.Payments.Contracts;
using Commerce.SharedKernel.Classification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Commerce.Workers.Notification;

public interface INotificationChannel
{
    string Name { get; }
    Task SendAsync(Contact contact, string template, Guid relatedId, CancellationToken cancellationToken);
}

internal sealed partial class LoggingEmailChannel(ILogger<LoggingEmailChannel> logger) : INotificationChannel
{
    public string Name => "email";

    public Task SendAsync(Contact contact, string template, Guid relatedId, CancellationToken cancellationToken)
    {
        LogSent(logger, template, contact.Email, relatedId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Template} sent to {Recipient} for {RelatedId}")]
    private static partial void LogSent(ILogger logger, string template, [PersonalData] string recipient, Guid relatedId);
}

internal sealed class RememberContact(NotificationDbContext db) : IIntegrationEventHandler<CustomerRegisteredV1>
{
    public async Task HandleAsync(CustomerRegisteredV1 e, MessageContext context, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.CustomerId == e.CustomerId, cancellationToken);
        if (contact is null)
        {
            db.Contacts.Add(new Contact { CustomerId = e.CustomerId, UserSubject = e.UserSubject, Email = e.Email, DisplayName = e.DisplayName });
        }
        else
        {
            contact.Email = e.Email;
            contact.DisplayName = e.DisplayName;
        }
    }
}

internal abstract class NotifyCustomer<TEvent>(NotificationDbContext db, INotificationChannel channel, TimeProvider clock) : IIntegrationEventHandler<TEvent>
    where TEvent : SharedKernel.Messaging.IntegrationEvent
{
    protected abstract string Template { get; }
    protected abstract Guid CustomerId(TEvent e);
    protected abstract Guid RelatedId(TEvent e);

    public async Task HandleAsync(TEvent e, MessageContext context, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerId == CustomerId(e), cancellationToken)
            ?? throw new InvalidOperationException($"No contact for customer {CustomerId(e)} yet; retrying later.");
        await channel.SendAsync(contact, Template, RelatedId(e), cancellationToken);
        db.Notifications.Add(new SentNotification
        {
            Id = Guid.CreateVersion7(), CustomerId = contact.CustomerId, Template = Template, Channel = channel.Name,
            Status = "sent", RelatedId = RelatedId(e), CreatedAt = clock.GetUtcNow(),
        });
    }
}

internal sealed class NotifyOrderConfirmed(NotificationDbContext db, INotificationChannel channel, TimeProvider clock) : NotifyCustomer<OrderConfirmedV1>(db, channel, clock)
{
    protected override string Template => "order-confirmed";
    protected override Guid CustomerId(OrderConfirmedV1 e) => e.CustomerId;
    protected override Guid RelatedId(OrderConfirmedV1 e) => e.OrderId;
}

internal sealed class NotifyOrderRejected(NotificationDbContext db, INotificationChannel channel, TimeProvider clock) : NotifyCustomer<OrderRejectedV1>(db, channel, clock)
{
    protected override string Template => "order-rejected";
    protected override Guid CustomerId(OrderRejectedV1 e) => e.CustomerId;
    protected override Guid RelatedId(OrderRejectedV1 e) => e.OrderId;
}

internal sealed class NotifyOrderCancelled(NotificationDbContext db, INotificationChannel channel, TimeProvider clock) : NotifyCustomer<OrderCancelledV1>(db, channel, clock)
{
    protected override string Template => "order-cancelled";
    protected override Guid CustomerId(OrderCancelledV1 e) => e.CustomerId;
    protected override Guid RelatedId(OrderCancelledV1 e) => e.OrderId;
}

internal sealed class NotifyOrderFulfilled(NotificationDbContext db, INotificationChannel channel, TimeProvider clock) : NotifyCustomer<OrderFulfilledV1>(db, channel, clock)
{
    protected override string Template => "order-shipped";
    protected override Guid CustomerId(OrderFulfilledV1 e) => e.CustomerId;
    protected override Guid RelatedId(OrderFulfilledV1 e) => e.OrderId;
}
