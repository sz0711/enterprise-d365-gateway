using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using enterprise_d365_gateway.Functions;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Tests.Integration;

/// <summary>
/// Tests ServiceBusUpsertTrigger message processing logic.
/// ServiceBus trigger accepts a raw string + FunctionContext (easier to mock than HttpRequestData).
/// </summary>
public class ServiceBusUpsertTriggerIntegrationTests
{
    private readonly Mock<IDataverseUpsertService> _upsertServiceMock = new();
    private readonly Mock<ILogger<ServiceBusUpsertTrigger>> _loggerMock = new();
    private readonly Mock<FunctionContext> _contextMock = new();
    private readonly ServiceBusUpsertTrigger _sut;

    public ServiceBusUpsertTriggerIntegrationTests()
    {
        _contextMock.SetupGet(c => c.InvocationId).Returns("test-invocation-001");
        _contextMock.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        _sut = new ServiceBusUpsertTrigger(_upsertServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task RunAsync_ValidMessage_AllSuccess_Completes()
    {
        var results = new List<UpsertResult>
        {
            new() { Id = Guid.NewGuid(), Created = true, ErrorCategory = ErrorCategory.None }
        };
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);

        var message = """{"Payloads":[{"EntityLogicalName":"account","UpsertKey":"ACC-001","Attributes":{"name":"Test"}}]}""";

        await _sut.RunAsync(message, _contextMock.Object);

        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_InvalidJson_LogsErrorNoThrow()
    {
        var message = "not valid json {{{";

        // Should not throw — invalid messages are discarded
        await _sut.RunAsync(message, _contextMock.Object);

        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_EmptyPayloads_LogsWarningNoThrow()
    {
        var message = """{"Payloads":[]}""";

        await _sut.RunAsync(message, _contextMock.Object);

        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_TechnicalFailures_ThrowsInvalidOperationException()
    {
        var results = new List<UpsertResult>
        {
            new() { Id = Guid.Empty, ErrorCategory = ErrorCategory.Transient, ErrorMessage = "Service error" }
        };
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);

        var message = """{"Payloads":[{"EntityLogicalName":"account","UpsertKey":"ACC-001","Attributes":{"name":"Test"}}]}""";

        var act = async () => await _sut.RunAsync(message, _contextMock.Object);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*technical*");
    }

    [Fact]
    public async Task RunAsync_ValidationFailuresOnly_NoThrow()
    {
        var results = new List<UpsertResult>
        {
            new() { Id = Guid.Empty, ErrorCategory = ErrorCategory.Validation, ErrorMessage = "Bad data" }
        };
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);

        var message = """{"Payloads":[{"EntityLogicalName":"account","UpsertKey":"ACC-001","Attributes":{"name":"Test"}}]}""";

        // Should not throw — validation-only failures are logged as warnings
        await _sut.RunAsync(message, _contextMock.Object);
    }
}
