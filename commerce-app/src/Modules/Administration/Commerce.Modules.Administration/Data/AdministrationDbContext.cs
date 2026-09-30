// Persistence of the Administration module (schema `administration`) and the feature
// definition provider that combines defaults with database overrides.
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Administration.Contracts;
using Commerce.Modules.Administration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;

namespace Commerce.Modules.Administration.Data;

public sealed class AdministrationDbContext(DbContextOptions<AdministrationDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "administration";
    public override string Schema => SchemaName;

    public DbSet<FeatureFlagOverride> FeatureFlags => Set<FeatureFlagOverride>();
}

internal sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlagOverride>
{
    public void Configure(EntityTypeBuilder<FeatureFlagOverride> flag)
    {
        flag.ToTable("feature_flags");
        flag.HasKey(f => f.Id);
        flag.Property(f => f.Id).HasColumnName("name").HasMaxLength(60);
        flag.Property(f => f.Reason).HasMaxLength(200);
        flag.Property(f => f.UpdatedBy).HasMaxLength(100);
        flag.Property(f => f.Version).IsConcurrencyToken();
    }
}

internal sealed class AdministrationDesignTimeFactory() : DesignTimeFactory<AdministrationDbContext>(AdministrationDbContext.SchemaName);

// Feeds Microsoft.FeatureManagement: a flag is on or off according to its override, or its
// default when no override exists. Overrides are cached briefly so checking a flag does not
// query the database on every request; a change takes effect within the cache lifetime.
public sealed class DatabaseFeatureDefinitionProvider(IServiceScopeFactory scopes, TimeProvider clock) : IFeatureDefinitionProvider, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IReadOnlyDictionary<string, bool> _cached = new Dictionary<string, bool>();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public async IAsyncEnumerable<FeatureDefinition> GetAllFeatureDefinitionsAsync()
    {
        var values = await CurrentValuesAsync(CancellationToken.None);
        foreach (var name in FeatureFlags.All)
        {
            yield return Definition(name, values);
        }
    }

    public async Task<FeatureDefinition> GetFeatureDefinitionAsync(string featureName)
    {
        var values = await CurrentValuesAsync(CancellationToken.None);
        return Definition(featureName, values);
    }

    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    public void Dispose() => _refresh.Dispose();

    private static FeatureDefinition Definition(string name, IReadOnlyDictionary<string, bool> values) => new()
    {
        Name = name,
        // An empty filter list means "off"; the built-in AlwaysOn filter means "on".
        EnabledFor = values.GetValueOrDefault(name) ? [new FeatureFilterConfiguration { Name = "AlwaysOn" }] : [],
    };

    public async Task<IReadOnlyDictionary<string, bool>> CurrentValuesAsync(CancellationToken cancellationToken)
    {
        if (clock.GetUtcNow() - _loadedAt < CacheLifetime)
        {
            return _cached;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AdministrationDbContext>();
            var overrides = await db.FeatureFlags.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Enabled, cancellationToken);
            _cached = FeatureFlagDefaults.Values.ToDictionary(pair => pair.Key, pair => overrides.GetValueOrDefault(pair.Key, pair.Value), StringComparer.Ordinal);
            _loadedAt = clock.GetUtcNow();
            return _cached;
        }
        finally
        {
            _refresh.Release();
        }
    }
}
