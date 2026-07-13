using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using enterprise_d365_gateway.Functions;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Integration;

/// <summary>
/// Request guards of the generic upsert endpoint: the body-size limit must hold
/// on NON-seekable streams (the production transport shape), batch-level
/// MaxLookupDepth must reach the payloads, and error bodies must be JSON.
/// </summary>
public class HttpUpsertTriggerGuardTests
{
    private readonly Mock<IDataverseUpsertService> _upsertServiceMock = new();
    private readonly Mock<FunctionContext> _contextMock = new();
    private readonly DataverseOptions _options = new();
    private readonly HttpUpsertTrigger _sut;

    public HttpUpsertTriggerGuardTests()
    {
        _contextMock.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpsertResult> { new() { ErrorCategory = ErrorCategory.None } });
        _sut = new HttpUpsertTrigger(
            _upsertServiceMock.Object,
            new ResultMapper(),
            new Mock<ILogger<HttpUpsertTrigger>>().Object,
            Options.Create(_options));
    }

    /// <summary>Read-only, non-seekable stream — mimics the production transport.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NonSeekableBodyOverLimit_Returns413()
    {
        _options.MaxRequestBytes = 64;
        var bigBody = "{\"Payloads\":[{\"EntityLogicalName\":\"account\",\"KeyAttributes\":{\"accountnumber\":\""
            + new string('x', 500) + "\"},\"Attributes\":{}}]}";
        var req = new FakeHttpRequestData(_contextMock.Object, new NonSeekableStream(Encoding.UTF8.GetBytes(bigBody)));

        var response = await _sut.RunAsync(req);

        ((int)response.StatusCode).Should().Be(413, "the limit must hold even when Body.Length is unavailable");
        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeclaredContentLengthOverLimit_Returns413BeforeReading()
    {
        _options.MaxRequestBytes = 64;
        var req = new FakeHttpRequestData(_contextMock.Object, new NonSeekableStream(Encoding.UTF8.GetBytes("{}")));
        req.Headers.Add("Content-Length", "999999");

        var response = await _sut.RunAsync(req);

        ((int)response.StatusCode).Should().Be(413);
    }

    [Fact]
    public async Task BatchMaxLookupDepth_PropagatedToPayloads()
    {
        IEnumerable<UpsertPayload>? captured = null;
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<UpsertPayload>, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new List<UpsertResult> { new() { ErrorCategory = ErrorCategory.None } });

        var body = """{"MaxLookupDepth":2,"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"D-1"},"Attributes":{"name":"Depth"}}]}""";
        await _sut.RunAsync(new FakeHttpRequestData(_contextMock.Object, body));

        captured.Should().NotBeNull();
        captured!.Single().MaxLookupDepth.Should().Be(2);
    }

    [Fact]
    public async Task PayloadOwnMaxLookupDepth_NotOverriddenByBatch()
    {
        IEnumerable<UpsertPayload>? captured = null;
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<UpsertPayload>, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new List<UpsertResult> { new() { ErrorCategory = ErrorCategory.None } });

        var body = """{"MaxLookupDepth":2,"Payloads":[{"EntityLogicalName":"account","MaxLookupDepth":1,"KeyAttributes":{"accountnumber":"D-2"},"Attributes":{"name":"Depth"}}]}""";
        await _sut.RunAsync(new FakeHttpRequestData(_contextMock.Object, body));

        captured!.Single().MaxLookupDepth.Should().Be(1);
    }

    [Fact]
    public async Task MalformedJson_Returns400WithJsonErrorBody()
    {
        var response = await _sut.RunAsync(new FakeHttpRequestData(_contextMock.Object, "{{{ not json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("Content-Type", out var ct).Should().BeTrue();
        ct!.First().Should().StartWith("application/json");

        var body = ((FakeHttpResponseData)response).ReadBody();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("Error").GetString().Should().StartWith("Invalid request");
        doc.RootElement.GetProperty("CorrelationId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ThrottlingOnlyBatch_Returns429WithRetryAfter()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpsertResult> { new() { ErrorCategory = ErrorCategory.Throttling } });

        var body = """{"Payloads":[{"EntityLogicalName":"account","KeyAttributes":{"accountnumber":"T-1"},"Attributes":{"name":"T"}}]}""";
        var response = await _sut.RunAsync(new FakeHttpRequestData(_contextMock.Object, body));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.TryGetValues("Retry-After", out _).Should().BeTrue();
    }
}
