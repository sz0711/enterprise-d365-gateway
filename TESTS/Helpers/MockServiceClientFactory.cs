using FakeXrmEasy.Abstractions;
using FakeXrmEasy.Abstractions.Enums;
using FakeXrmEasy.Middleware;
using FakeXrmEasy.Middleware.Crud;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using MODEL;
using Moq;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Tests.Helpers;

/// <summary>
/// FakeXrmEasy-based in-memory Dataverse context for tests.
/// Provides a realistic IOrganizationServiceAsync2 backed by in-memory data.
/// </summary>
public class FakeDataverseContext
{
    public IXrmFakedContext Context { get; }
    public IOrganizationServiceAsync2 Service { get; }
    public Mock<IDataverseServiceClientFactory> FactoryMock { get; } = new();

    public FakeDataverseContext(params Entity[] seedData)
    {
        Context = MiddlewareBuilder
            .New()
            .AddCrud()
            .UseCrud()
            .SetLicense(FakeXrmEasyLicense.RPL_1_5)
            .Build();

        // Pre-register earlybound types to prevent race conditions in parallel tests
        Context.EnableProxyTypes(typeof(Account).Assembly);

        if (seedData.Length > 0)
            Context.Initialize(seedData);

        Service = Context.GetAsyncOrganizationService2();

        FactoryMock
            .Setup(f => f.GetOrCreateServiceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Service);
    }

    /// <summary>
    /// Seed additional entities into the in-memory context after construction.
    /// </summary>
    public void Initialize(params Entity[] entities)
    {
        Context.Initialize(entities);
    }
}

/// <summary>
/// Lightweight Moq-based mock for tests that don't need in-memory query execution.
/// </summary>
public class MockServiceClientFactory
{
    public Mock<IDataverseServiceClientFactory> FactoryMock { get; } = new();
    public Mock<IOrganizationServiceAsync2> ServiceMock { get; } = new();

    public MockServiceClientFactory()
    {
        FactoryMock
            .Setup(f => f.GetOrCreateServiceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ServiceMock.Object);
    }

    public void OnCreate(Guid returnId)
    {
        ServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(returnId);

        ServiceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                var response = new OrganizationResponse();
                response.Results["id"] = returnId;
                return response;
            });
    }

    public void OnUpdate()
    {
        ServiceMock
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ServiceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());
    }

    public void OnRetrieveMultiple(EntityCollection result)
    {
        ServiceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }
}
