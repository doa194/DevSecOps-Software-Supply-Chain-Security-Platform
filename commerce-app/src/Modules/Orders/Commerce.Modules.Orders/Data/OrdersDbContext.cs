// Persistence of the Orders module (schema `orders`).
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Modules.Orders.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "orders";
    public override string Schema => SchemaName;

    public DbSet<Order> Orders => Set<Order>();
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> order)
    {
        order.ToTable("orders");
        order.HasKey(o => o.Id);
        order.Property(o => o.OwnerSubject).HasMaxLength(64);
        order.HasIndex(o => new { o.OwnerSubject, o.PlacedAt });
        order.HasIndex(o => o.Status);
        order.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        order.Property(o => o.StatusReason).HasMaxLength(200);
        order.Property(o => o.Total).HasPrecision(12, 2);
        order.Property(o => o.Currency).HasMaxLength(3);
        order.Property(o => o.PaymentMethodToken).HasMaxLength(64);
        order.Property(o => o.Version).IsConcurrencyToken();
        order.OwnsMany(o => o.Lines, line =>
        {
            line.ToTable("order_lines");
            line.WithOwner().HasForeignKey("order_id");
            line.Property<int>("id").ValueGeneratedOnAdd();
            line.HasKey("id");
            line.Property(l => l.Sku).HasMaxLength(12);
            line.Property(l => l.Name).HasMaxLength(120);
            line.Property(l => l.UnitPrice).HasPrecision(12, 2);
            line.Ignore(l => l.LineTotal);
        });
        order.Navigation(o => o.Lines).HasField("_lines");
    }
}

internal sealed class OrdersDesignTimeFactory() : DesignTimeFactory<OrdersDbContext>(OrdersDbContext.SchemaName);
