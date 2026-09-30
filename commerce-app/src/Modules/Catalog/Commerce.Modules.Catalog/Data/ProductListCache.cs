// Redis cache for the public product listing, the most frequent anonymous request.
// Every catalog change increments a version number that is part of the cache key, so
// stale listings are never served after a change and old entries simply expire.
using System.Text.Json;
using StackExchange.Redis;

namespace Commerce.Modules.Catalog.Data;

public sealed class ProductListCache(IConnectionMultiplexer redis)
{
    private const string VersionKey = "catalog:products:version";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public async Task<T?> GetAsync<T>(string query)
    {
        var database = redis.GetDatabase();
        var version = (long?)await database.StringGetAsync(VersionKey) ?? 0;
        var cached = await database.StringGetAsync($"catalog:products:v{version}:{query}");
        return cached.HasValue ? JsonSerializer.Deserialize<T>(cached.ToString()) : default;
    }

    public async Task SetAsync<T>(string query, T value)
    {
        var database = redis.GetDatabase();
        var version = (long?)await database.StringGetAsync(VersionKey) ?? 0;
        await database.StringSetAsync($"catalog:products:v{version}:{query}", JsonSerializer.Serialize(value), Lifetime);
    }

    public Task InvalidateAsync() => redis.GetDatabase().StringIncrementAsync(VersionKey);
}
