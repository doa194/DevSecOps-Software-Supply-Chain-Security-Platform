// Persistence of the Inventory module (schema `inventory`).
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Modules.Inventory.Data;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "inventory";
    public override string Schema => SchemaName;

    public DbSet<StockItem> StockItems => Set<StockItem>();
}

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> item)
    {
        item.ToTable("stock_items");
        item.HasKey(i => i.Id);
        item.Property(i => i.Id).HasColumnName("sku").HasMaxLength(12);
        item.Property(i => i.Version).IsConcurrencyToken();
        item.Ignore(i => i.Reserved);
        item.Ignore(i => i.Available);
        item.Ignore(i => i.IsLow);
        item.OwnsMany(i => i.Reservations, reservation =>
        {
            reservation.ToTable("reservations");
            reservation.WithOwner().HasForeignKey("sku");
            // Database-generated surrogate key: a newly added reservation has no key yet, so
            // EF inserts it. (A key set by the domain would make EF assume the row exists.)
            reservation.Property<long>("row_id").ValueGeneratedOnAdd();
            reservation.HasKey("row_id");
            reservation.HasIndex("sku", nameof(Reservation.OrderId)).IsUnique();
        });
        item.Navigation(i => i.Reservations).HasField("_reservations");
    }
}

internal sealed class InventoryDesignTimeFactory() : DesignTimeFactory<InventoryDbContext>(InventoryDbContext.SchemaName);
