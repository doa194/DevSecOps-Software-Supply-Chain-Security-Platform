// The separate, event-driven read model for reporting.
//
// These tables are built only from integration events, never by reading other modules'
// tables. Reporting queries therefore never load the transactional database, and the
// projections can be rebuilt by replaying events. Each event is applied exactly once
// thanks to the inbox; order summaries also ignore events older than what they show.
using Commerce.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Workers.Reporting;

public sealed class OrderSummary
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset PlacedAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
}

public sealed class DailySales
{
    public DateOnly Day { get; init; }
    public string Currency { get; init; } = string.Empty;
    public int OrdersConfirmed { get; set; }
    public int OrdersCancelled { get; set; }
    public int OrdersRejected { get; set; }
    public decimal Revenue { get; set; }
    public decimal Refunds { get; set; }
}

public sealed class ProductSales
{
    public string Sku { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int UnitsSold { get; set; }
    public decimal Revenue { get; set; }
}

public sealed class StockLevel
{
    public string Sku { get; init; } = string.Empty;
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public int Available { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "reporting";
    public override string Schema => SchemaName;

    public DbSet<OrderSummary> OrderSummaries => Set<OrderSummary>();
    public DbSet<DailySales> DailySales => Set<DailySales>();
    public DbSet<ProductSales> ProductSales => Set<ProductSales>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
}

internal sealed class ReportingConfiguration :
    IEntityTypeConfiguration<OrderSummary>, IEntityTypeConfiguration<DailySales>, IEntityTypeConfiguration<ProductSales>, IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<OrderSummary> summary)
    {
        summary.ToTable("order_summaries");
        summary.HasKey(s => s.OrderId);
        summary.HasIndex(s => s.Status);
        summary.Property(s => s.Status).HasMaxLength(20);
        summary.Property(s => s.Total).HasPrecision(12, 2);
        summary.Property(s => s.Currency).HasMaxLength(3);
    }

    public void Configure(EntityTypeBuilder<DailySales> sales)
    {
        sales.ToTable("daily_sales");
        sales.HasKey(s => new { s.Day, s.Currency });
        sales.Property(s => s.Currency).HasMaxLength(3);
        sales.Property(s => s.Revenue).HasPrecision(14, 2);
        sales.Property(s => s.Refunds).HasPrecision(14, 2);
    }

    public void Configure(EntityTypeBuilder<ProductSales> product)
    {
        product.ToTable("product_sales");
        product.HasKey(p => p.Sku);
        product.Property(p => p.Sku).HasMaxLength(12);
        product.Property(p => p.Name).HasMaxLength(120);
        product.Property(p => p.Revenue).HasPrecision(14, 2);
    }

    public void Configure(EntityTypeBuilder<StockLevel> stock)
    {
        stock.ToTable("stock_levels");
        stock.HasKey(s => s.Sku);
        stock.Property(s => s.Sku).HasMaxLength(12);
    }
}

internal sealed class ReportingDesignTimeFactory() : DesignTimeFactory<ReportingDbContext>(ReportingDbContext.SchemaName);
