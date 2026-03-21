using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Extensions
{
    public static class DataverseServiceCollectionExtensions
    {
        public static IServiceCollection AddDataverseIntegration(this IServiceCollection services)
        {
            services.AddMemoryCache();

            services.AddSingleton<IDataverseTokenProvider, DataverseTokenProvider>();
            services.AddSingleton<IDataverseServiceClientFactory, DataverseServiceClientFactory>();
            services.AddSingleton<IEntityMappingCache, EntityMappingCache>();
            services.AddSingleton<IEarlyboundEntityMapper, EarlyboundEntityMapper>();
            services.AddSingleton<IDataverseUpsertService, DataverseUpsertService>();

            return services;
        }
    }
}
