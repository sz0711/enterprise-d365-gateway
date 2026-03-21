using Microsoft.Extensions.Caching.Memory;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Services
{
    public class EntityMappingCache : IEntityMappingCache
    {
        private readonly IMemoryCache _cache;

        public EntityMappingCache(IMemoryCache cache)
        {
            _cache = cache;
        }

        private string GetCacheKey(string entityLogicalName, string externalSystemId)
            => $"EntityMapping::{entityLogicalName.ToLowerInvariant()}::{externalSystemId.ToLowerInvariant()}";

        public Task<Guid?> GetAsync(string entityLogicalName, string externalSystemId, CancellationToken cancellationToken = default)
        {
            var key = GetCacheKey(entityLogicalName, externalSystemId);
            return Task.FromResult(_cache.TryGetValue<Guid>(key, out var id) ? (Guid?)id : null);
        }

        public Task SetAsync(string entityLogicalName, string externalSystemId, Guid id, CancellationToken cancellationToken = default)
        {
            var key = GetCacheKey(entityLogicalName, externalSystemId);
            _cache.Set(key, id, TimeSpan.FromHours(2));
            return Task.CompletedTask;
        }

        public async Task<Guid> GetOrAddAsync(string entityLogicalName, string externalSystemId, Func<Task<Guid>> factory, CancellationToken cancellationToken = default)
        {
            var key = GetCacheKey(entityLogicalName, externalSystemId);
            if (_cache.TryGetValue<Guid>(key, out var existing))
            {
                return existing;
            }

            var value = await factory();
            _cache.Set(key, value, TimeSpan.FromHours(2));
            return value;
        }
    }
}
