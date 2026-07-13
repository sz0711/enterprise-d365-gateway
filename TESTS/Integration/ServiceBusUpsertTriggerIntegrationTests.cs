using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using enterprise_d365_gateway.Functions;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Tests.Integration;

/// <summary>
/// Tests ServiceBusUpsertTrigger message processing logic.
/// ServiceBus trigger accepts a raw string + FunctionContext (easier to mock than HttpRequestData).
/// Poison-message policy: malformed, empty, oversized and failed messages THROW so
/// Service Bus retries and eventually dead-letters them — never silent data loss.
/// </summary>
public class ServiceBusUpsertTriggerIntegrationTests
{
    private readonly Mock<IDataverseUpsertService> _upsertServiceMock = new();
    private readonly Mock<ILogger<ServiceBusUpsertTrigger>> _loggerMock = new();
    private readonly Mock<FunctionContext> _contextMock = new();
    private readonly DataverseOptions _options = new();
    private readonly ServiceBusUpsertTrigger _sut;

    public ServiceBusUpsertTriggerIntegrationTests()
    {
        _contextMock.SetupGet(c => c.InvocationId).Returns("test-invocation-001");
        _contextMock.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        _sut = new ServiceBusUpsertTrigger(_upsertServiceMock.Object, _loggerMock.Object, Options.Create(_options));
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

        var message = """{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACC-001"},"Attributes":{"name":"Test"}}]}""";

        await _sut.RunAsync(message, _contextMock.Object);

        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_InvalidJson_ThrowsSoMessageDeadLetters()
    {
        var message = "not valid json {{{";

        var act = async () => await _sut.RunAsync(message, _contextMock.Object);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not valid JSON*");
        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_EmptyPayloads_ThrowsSoMessageDeadLetters()
    {
        var message = """{"Payloads":[]}""";

        var act = async () => await _sut.RunAsync(message, _contextMock.Object);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no payloads*");
        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_BatchTooLarge_ThrowsSoMessageDeadLetters()
    {
        _options.MaxBatchItems = 1;
        var message = """
        {"Payloads":[
            {"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"A-1"},"Attributes":{"name":"One"}},
            {"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"A-2"},"Attributes":{"name":"Two"}}
        ]}
        """;

        var act = async () => await _sut.RunAsync(message, _contextMock.Object);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds the maximum*");
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

        var message = """{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACC-001"},"Attributes":{"name":"Test"}}]}""";

        var act = async () => await _sut.RunAsync(message, _contextMock.Object);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*technical*");
    }

    [Fact]
    public async Task RunAsync_ValidationFailures_ThrowSoMessageDeadLetters()
    {
        var results = new List<UpsertResult>
        {
            new() { Id = Guid.Empty, ErrorCategory = ErrorCategory.Validation, ErrorMessage = "Bad data" }
        };
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);

        var message = """{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACC-001"},"Attributes":{"name":"Test"}}]}""";

        var act = async () => await _sut.RunAsync(message, _contextMock.Object);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validation*");
    }

    [Fact]
    public async Task RunAsync_BatchLevelMaxLookupDepth_PropagatedToPayloads()
    {
        IEnumerable<UpsertPayload>? captured = null;
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<UpsertPayload>, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new List<UpsertResult> { new() { ErrorCategory = ErrorCategory.None } });

        var message = """{"MaxLookupDepth":2,"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACC-001"},"Attributes":{"name":"Test"}}]}""";

        await _sut.RunAsync(message, _contextMock.Object);

        captured.Should().NotBeNull();
        captured!.Single().MaxLookupDepth.Should().Be(2);
    }
}
