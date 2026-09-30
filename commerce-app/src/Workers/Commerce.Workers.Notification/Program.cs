// Notification worker: notifies customers about their orders and lets them list their
// notifications (routed through the gateway at /api/notifications).
using Commerce.BuildingBlocks.Health;
using Commerce.BuildingBlocks.Hosting;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Security;
using Commerce.Modules.Customers.Contracts;
using Commerce.Modules.Orders.Contracts;
using Commerce.Workers.Notification;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerceServiceDefaults("notification-worker");
builder.Services.AddCommerceMessaging(builder.Configuration, typeof(CustomerRegisteredV1).Assembly, typeof(OrderConfirmedV1).Assembly);
builder.Services.AddModuleDbContext<NotificationDbContext>(builder.Configuration, NotificationDbContext.SchemaName);
builder.Services.AddSingleton<INotificationChannel, LoggingEmailChannel>();
builder.Services.AddIntegrationEventConsumer("notification-worker.notifications", consumer => consumer
    .Handle<CustomerRegisteredV1, RememberContact, NotificationDbContext>()
    .Handle<OrderConfirmedV1, NotifyOrderConfirmed, NotificationDbContext>()
    .Handle<OrderRejectedV1, NotifyOrderRejected, NotificationDbContext>()
    .Handle<OrderCancelledV1, NotifyOrderCancelled, NotificationDbContext>()
    .Handle<OrderFulfilledV1, NotifyOrderFulfilled, NotificationDbContext>());
builder.Services.AddHealthChecks().AddRabbitMqReadiness();

var app = builder.Build();

if (args.Contains("migrate"))
{
    await MigrationRunner.MigrateAsync(app.Services, app.Configuration,
        [new(typeof(NotificationDbContext), NotificationDbContext.SchemaName, "commerce_notification_worker")], app.Logger, CancellationToken.None);
    return;
}

app.UseCommerceServiceDefaults();

app.MapGet("/api/notifications/mine", async (NotificationDbContext db, ICurrentUser user, CancellationToken cancellationToken) =>
{
    var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.UserSubject == user.Subject, cancellationToken);
    if (contact is null)
    {
        return TypedResults.Ok(new List<SentNotification>());
    }

    var notifications = await db.Notifications.AsNoTracking().Where(n => n.CustomerId == contact.CustomerId)
        .OrderByDescending(n => n.CreatedAt).Take(50).ToListAsync(cancellationToken);
    return TypedResults.Ok(notifications);
}).RequireAuthorization(Permissions.OrdersPlace).WithTags("Notifications").WithName("MyNotifications");

await app.RunAsync();
