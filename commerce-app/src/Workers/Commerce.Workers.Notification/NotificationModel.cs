// Notification worker model: a minimal contact projection (so the worker never reads
// the Customers tables) and a record of every notification sent.
using Commerce.BuildingBlocks.Persistence;
using Commerce.SharedKernel.Classification;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Workers.Notification;

public sealed class Contact
{
    public Guid CustomerId { get; init; }
    public required string UserSubject { get; init; }
    [PersonalData] public required string Email { get; set; }
    [PersonalData] public required string DisplayName { get; set; }
}

public sealed class SentNotification
{
    public Guid Id { get; init; }
    public Guid CustomerId { get; init; }
    public required string Template { get; init; }
    public required string Channel { get; init; }
    public required string Status { get; init; }
    public Guid? RelatedId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "notifications";
    public override string Schema => SchemaName;

    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<SentNotification> Notifications => Set<SentNotification>();
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Contact>, IEntityTypeConfiguration<SentNotification>
{
    public void Configure(EntityTypeBuilder<Contact> contact)
    {
        contact.ToTable("contacts");
        contact.HasKey(c => c.CustomerId);
        contact.HasIndex(c => c.UserSubject).IsUnique();
        contact.Property(c => c.UserSubject).HasMaxLength(64);
        contact.Property(c => c.Email).HasMaxLength(254);
        contact.Property(c => c.DisplayName).HasMaxLength(100);
    }

    public void Configure(EntityTypeBuilder<SentNotification> notification)
    {
        notification.ToTable("notifications");
        notification.HasKey(n => n.Id);
        notification.HasIndex(n => n.CustomerId);
        notification.Property(n => n.Template).HasMaxLength(60);
        notification.Property(n => n.Channel).HasMaxLength(20);
        notification.Property(n => n.Status).HasMaxLength(20);
    }
}

internal sealed class NotificationDesignTimeFactory() : DesignTimeFactory<NotificationDbContext>(NotificationDbContext.SchemaName);
