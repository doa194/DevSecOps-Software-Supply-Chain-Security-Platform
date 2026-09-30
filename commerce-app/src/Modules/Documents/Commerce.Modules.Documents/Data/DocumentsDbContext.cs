// Persistence of the Documents module (schema `documents`).
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Documents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Modules.Documents.Data;

public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "documents";
    public override string Schema => SchemaName;

    public DbSet<Document> Documents => Set<Document>();
}

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> document)
    {
        document.ToTable("documents");
        document.HasKey(d => d.Id);
        document.HasIndex(d => d.CustomerId);
        document.HasIndex(d => new { d.OrderId, d.Kind });
        document.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
        document.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        document.Property(d => d.FileName).HasMaxLength(120);
        document.Property(d => d.ContentType).HasMaxLength(60);
        document.Property(d => d.Sha256).HasMaxLength(64);
        document.Property(d => d.StorageKey).HasMaxLength(200);
        document.Property(d => d.FailureReason).HasMaxLength(200);
        document.Property(d => d.Version).IsConcurrencyToken();
    }
}

internal sealed class DocumentsDesignTimeFactory() : DesignTimeFactory<DocumentsDbContext>(DocumentsDbContext.SchemaName);
