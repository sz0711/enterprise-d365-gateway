using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class EntityMappingCache : IEntityMappingCache
    {
        private readonly IMemoryCache _cache;
        private readonly DataverseOptions _options;

        public EntityMappingCache(IMemoryCache cache, IOptions<DataverseOptions> options)
        {
            _cache = cache;
            _options = options.Value;
        }

        private static string GetCacheKey(string entityLogicalName, string externalIdAttribute, string normalizedValue)
            => $"EntityMapping::{entityLogicalName.ToLowerInvariant()}::{externalIdAttribute.ToLowerInvariant()}::{normalizedValue.Trim().ToLowerInvariant()}";

        public Task<Guid?> GetAsync(string entityLogicalName, string externalIdAttribute, string normalizedValue, CancellationToken cancellationToken = default)
        {
            var key = GetCacheKey(entityLogicalName, externalIdAttribute, normalizedValue);
            return Task.FromResult(_cache.TryGetValue<Guid>(key, out var id) ? (Guid?)id : null);
        }

        public Task SetAsync(string entityLogicalName, string externalIdAttribute, string normalizedValue, Guid id, CancellationToken cancellationToken = default)
        {
            var key = GetCacheKey(entityLogicalName, externalIdAttribute, normalizedValue);
            var entryOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(_options.CacheSlidingExpirationMinutes),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.CacheAbsoluteExpirationMinutes),
                Size = _options.CacheEntrySizeBytes,
                Priority = CacheItemPriority.Normal
            };
            _cache.Set(key, id, entryOptions);
            return Task.CompletedTask;
        }

        public void Remove(string entityLogicalName, string externalIdAttribute, string normalizedValue)
        {
            var key = GetCacheKey(entityLogicalName, externalIdAttribute, normalizedValue);
            _cache.Remove(key);
        }
    }
}
