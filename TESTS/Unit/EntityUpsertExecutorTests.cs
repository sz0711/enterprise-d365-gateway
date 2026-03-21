using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

public class EntityUpsertExecutorTests
{
    private static DataverseOptions DefaultOptions(Dictionary<string, string>? bypassStepIds = null) => new()
    {
        Url = "https://test.crm.dynamics.com",
        MaxRequestsPerSecond = 1000,
        MaxRetries = 1,
        RetryBaseDelayMs = 50,
        TimeoutPerOperationSeconds = 30,
        CircuitBreakerFailureThreshold = 10,
        CircuitBreakerSamplingDurationSeconds = 60,
        CircuitBreakerBreakDurationSeconds = 30,
        BypassPluginStepIds = bypassStepIds ?? new Dictionary<string, string>()
    };

    private static (EntityUpsertExecutor Sut, MockServiceClientFactory Mock) CreateSut(
        DataverseOptions? options = null)
    {
        var opts = options ?? DefaultOptions();
        var mockFactory = new MockServiceClientFactory();
        var logger = new Mock<ILogger<EntityUpsertExecutor>>();

        var sut = new EntityUpsertExecutor(
            mockFactory.FactoryMock.Object,
            logger.Object,
            Options.Create(opts));

        return (sut, mockFactory);
    }

    [Fact]
    public async Task CreateAsync_WithoutBypass_CallsCreateAsync()
    {
        var (sut, mockFactory) = CreateSut();
        var expectedId = Guid.NewGuid();
        var entity = new Entity("account");

        mockFactory.ServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var result = await sut.CreateAsync(entity);

        result.Should().Be(expectedId);
        mockFactory.ServiceMock.Verify(s => s.CreateAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithBypass_UsesExecuteAsyncWithBypassParameter()
    {
        var bypass = new Dictionary<string, string> { ["account"] = "step-guid-1,step-guid-2" };
        var (sut, mockFactory) = CreateSut(DefaultOptions(bypass));
        var expectedId = Guid.NewGuid();
        var entity = new Entity("account");

        mockFactory.ServiceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateResponse { Results = { ["id"] = expectedId } });

        var result = await sut.CreateAsync(entity);

        result.Should().Be(expectedId);
        mockFactory.ServiceMock.Verify(
            s => s.ExecuteAsync(
                It.Is<CreateRequest>(r =>
                    r.Parameters.ContainsKey("BypassBusinessLogicExecutionStepIds") &&
                    (string)r.Parameters["BypassBusinessLogicExecutionStepIds"] == "step-guid-1,step-guid-2"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WithoutBypass_CallsUpdateAsync()
    {
        var (sut, mockFactory) = CreateSut();
        var entity = new Entity("account") { Id = Guid.NewGuid() };

        mockFactory.ServiceMock
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.UpdateAsync(entity);

        mockFactory.ServiceMock.Verify(s => s.UpdateAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WithBypass_UsesExecuteAsyncWithBypassParameter()
    {
        var bypass = new Dictionary<string, string> { ["account"] = "step-guid-1" };
        var (sut, mockFactory) = CreateSut(DefaultOptions(bypass));
        var entity = new Entity("account") { Id = Guid.NewGuid() };

        mockFactory.ServiceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResponse());

        await sut.UpdateAsync(entity);

        mockFactory.ServiceMock.Verify(
            s => s.ExecuteAsync(
                It.Is<UpdateRequest>(r =>
                    r.Parameters.ContainsKey("BypassBusinessLogicExecutionStepIds") &&
                    (string)r.Parameters["BypassBusinessLogicExecutionStepIds"] == "step-guid-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetrieveMultipleAsync_ReturnsEntityCollection()
    {
        var (sut, mockFactory) = CreateSut();
        var expected = new EntityCollection();
        expected.Entities.Add(new Entity("account") { Id = Guid.NewGuid() });

        mockFactory.ServiceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var query = new QueryExpression("account") { ColumnSet = new ColumnSet(false) };
        var result = await sut.RetrieveMultipleAsync(query);

        result.Entities.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateAsync_EntityWithoutBypassConfig_UsesDirectCreate()
    {
        // Bypass configured for "account" but we create a "contact"
        var bypass = new Dictionary<string, string> { ["account"] = "step-guid-1" };
        var (sut, mockFactory) = CreateSut(DefaultOptions(bypass));
        var expectedId = Guid.NewGuid();
        var entity = new Entity("contact");

        mockFactory.ServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var result = await sut.CreateAsync(entity);

        result.Should().Be(expectedId);
        mockFactory.ServiceMock.Verify(
            s => s.CreateAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
        mockFactory.ServiceMock.Verify(
            s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
