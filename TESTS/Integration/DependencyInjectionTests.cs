using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Extensions;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Tests.Integration;

public class DependencyInjectionTests
{
    [Fact]
    public void AddDataverseIntegration_RegistersAllServices()
    {
        var services = new ServiceCollection();

        // Add logging and configuration (required by services)
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        // Add options with valid configuration
        services.Configure<DataverseOptions>(opts =>
        {
            opts.Url = "https://test.crm.dynamics.com";
            opts.MaxRequestsPerSecond = 100;
            opts.MaxRetries = 2;
            opts.RetryBaseDelayMs = 100;
            opts.TimeoutPerOperationSeconds = 30;
            opts.CircuitBreakerFailureThreshold = 5;
            opts.CircuitBreakerSamplingDurationSeconds = 30;
            opts.CircuitBreakerBreakDurationSeconds = 15;
        });

        services.AddDataverseIntegration();

        using var provider = services.BuildServiceProvider();

        // Verify all interfaces can be resolved
        provider.GetService<IEntityMappingCache>().Should().NotBeNull();
        provider.GetService<IEarlyboundEntityMapper>().Should().NotBeNull();
        provider.GetService<IRequestValidator>().Should().NotBeNull();
        provider.GetService<IExternalIdResolver>().Should().NotBeNull();
        provider.GetService<ILookupResolver>().Should().NotBeNull();
        provider.GetService<IEntityUpsertExecutor>().Should().NotBeNull();
        provider.GetService<IAdaptiveConcurrencyLimiter>().Should().NotBeNull();
        provider.GetService<IUpsertLockCoordinator>().Should().NotBeNull();
        provider.GetService<IErrorClassifier>().Should().NotBeNull();
        provider.GetService<IResultMapper>().Should().NotBeNull();
        provider.GetService<IDataverseUpsertService>().Should().NotBeNull();
    }

    [Fact]
    public void AddDataverseIntegration_AllServicesAreSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<DataverseOptions>(opts =>
        {
            opts.Url = "https://test.crm.dynamics.com";
        });

        services.AddDataverseIntegration();

        // Verify all registrations are Singleton
        var serviceTypes = new[]
        {
            typeof(IEntityMappingCache),
            typeof(IEarlyboundEntityMapper),
            typeof(IRequestValidator),
            typeof(IExternalIdResolver),
            typeof(ILookupResolver),
            typeof(IEntityUpsertExecutor),
            typeof(IAdaptiveConcurrencyLimiter),
            typeof(IUpsertLockCoordinator),
            typeof(IErrorClassifier),
            typeof(IResultMapper),
            typeof(IDataverseUpsertService)
        };

        foreach (var serviceType in serviceTypes)
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
            descriptor.Should().NotBeNull($"service {serviceType.Name} should be registered");
            descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton, $"{serviceType.Name} should be Singleton");
        }
    }
}
