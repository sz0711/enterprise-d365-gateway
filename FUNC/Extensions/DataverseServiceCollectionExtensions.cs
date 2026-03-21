using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Extensions
{
    public static class DataverseServiceCollectionExtensions
    {
        public static IServiceCollection AddDataverseIntegration(this IServiceCollection services)
        {
            // RAM-based cache with SizeLimit computed from available memory
            services.AddSingleton<IMemoryCache>(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<DataverseOptions>>().Value;
                var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var percentBudgetBytes = (long)(availableMemory * opts.CacheMemoryBudgetPercent / 100.0);
                var minBudgetBytes = opts.CacheMemoryBudgetMinMb * 1024L * 1024L;
                var maxBudgetBytes = opts.CacheMemoryBudgetMaxMb * 1024L * 1024L;

                if (maxBudgetBytes < minBudgetBytes)
                {
                    maxBudgetBytes = minBudgetBytes;
                }

                var budgetBytes = Math.Clamp(percentBudgetBytes, minBudgetBytes, maxBudgetBytes);

                return new MemoryCache(new MemoryCacheOptions
                {
                    SizeLimit = budgetBytes,
                    CompactionPercentage = 0.25
                });
            });

            // Infrastructure
            services.AddSingleton<IDataverseTokenProvider, DataverseTokenProvider>();
            services.AddSingleton<IDataverseServiceClientFactory, DataverseServiceClientFactory>();

            // Cache
            services.AddSingleton<IEntityMappingCache, EntityMappingCache>();

            // Mapping and Validation
            services.AddSingleton<IEarlyboundEntityMapper, EarlyboundEntityMapper>();
            services.AddSingleton<IRequestValidator, RequestValidator>();

            // Resolvers
            services.AddSingleton<IExternalIdResolver, ExternalIdResolver>();
            services.AddSingleton<ILookupResolver, LookupResolver>();

            // Execution and Resilience
            services.AddSingleton<IAdaptiveConcurrencyLimiter, AdaptiveConcurrencyLimiter>();
            services.AddSingleton<IEntityUpsertExecutor, EntityUpsertExecutor>();

            // Concurrency
            services.AddSingleton<IUpsertLockCoordinator, UpsertLockCoordinator>();

            // Error handling
            services.AddSingleton<IErrorClassifier, ErrorClassifier>();
            services.AddSingleton<IResultMapper, ResultMapper>();

            // Orchestrator (replaces DataverseUpsertService)
            services.AddSingleton<IDataverseUpsertService, UpsertOrchestrator>();

            // Health
            services.AddSingleton<IHealthCheckService, HealthCheckService>();

            return services;
        }
    }
}
