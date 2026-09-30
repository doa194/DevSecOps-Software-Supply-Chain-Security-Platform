// Persistence of the Identity module (schema `identity`).
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Modules.Identity.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "identity";
    public override string Schema => SchemaName;

    public DbSet<UserAccount> Accounts => Set<UserAccount>();
}

internal sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> account)
    {
        account.ToTable("user_accounts");
        account.HasKey(a => a.Id);
        account.Property(a => a.Subject).HasMaxLength(64);
        account.HasIndex(a => a.Subject).IsUnique();
        account.Property(a => a.Username).HasMaxLength(100);
        account.Property(a => a.Version).IsConcurrencyToken();
    }
}

internal sealed class IdentityDesignTimeFactory() : DesignTimeFactory<IdentityDbContext>(IdentityDbContext.SchemaName);
