using System.Net;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using enterprise_d365_gateway.Functions;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Integration;

public class HttpUpsertTriggerIntegrationTests
{
    private readonly Mock<IDataverseUpsertService> _upsertServiceMock = new();
    private readonly Mock<IResultMapper> _resultMapperMock = new();
    private readonly Mock<ILogger<HttpUpsertTrigger>> _loggerMock = new();
    private readonly Mock<FunctionContext> _contextMock = new();
    private readonly HttpUpsertTrigger _sut;

    public HttpUpsertTriggerIntegrationTests()
    {
        _contextMock.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        _sut = new HttpUpsertTrigger(_upsertServiceMock.Object, _resultMapperMock.Object, _loggerMock.Object);
    }

    private FakeHttpRequestData CreateRequest(string body)
    {
        return new FakeHttpRequestData(_contextMock.Object, body);
    }

    [Fact]
    public async Task RunAsync_ValidPayload_ReturnsResults()
    {
        var results = new List<UpsertResult>
        {
            new() { Id = Guid.NewGuid(), Created = true, ErrorCategory = ErrorCategory.None }
        };
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);
        _resultMapperMock
            .Setup(m => m.DetermineBatchStatusCode(It.IsAny<IReadOnlyList<UpsertResult>>()))
            .Returns(HttpStatusCode.OK);

        var json = """{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACC-001"},"Attributes":{"name":"Test"}}]}""";
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().Contain(h => h.Key == "x-correlation-id");
    }

    [Fact]
    public async Task RunAsync_InvalidJson_Returns400()
    {
        var req = CreateRequest("not valid json {{{");

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunAsync_EmptyPayloads_Returns400()
    {
        var req = CreateRequest("""{"Payloads":[]}""");

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunAsync_NullPayloads_Returns400()
    {
        var req = CreateRequest("{}");

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunAsync_ServiceThrows_Returns500()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        var json = """{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACC-001"},"Attributes":{"name":"Test"}}]}""";
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task RunAsync_CorrelationIdFromHeader_PreservedInResponse()
    {
        var results = new List<UpsertResult>
        {
            new() { Id = Guid.NewGuid(), Created = true, ErrorCategory = ErrorCategory.None }
        };
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);
        _resultMapperMock
            .Setup(m => m.DetermineBatchStatusCode(It.IsAny<IReadOnlyList<UpsertResult>>()))
            .Returns(HttpStatusCode.OK);

        var json = """{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"ACC-001"},"Attributes":{"name":"Test"}}]}""";
        var req = CreateRequest(json);
        req.Headers.Add("x-correlation-id", "test-correlation-123");

        var response = await _sut.RunAsync(req);

        response.Headers.TryGetValues("x-correlation-id", out var values).Should().BeTrue();
        values!.First().Should().Be("test-correlation-123");
    }
}
