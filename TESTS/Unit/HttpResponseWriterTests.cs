using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Moq;
using enterprise_d365_gateway.Functions;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

public class HttpResponseWriterTests
{
    private readonly Mock<FunctionContext> _contextMock = new();

    private FakeHttpRequestData CreateRequest() => new(_contextMock.Object, "{}");

    [Fact]
    public void ResolveCorrelationId_ValidHeader_Echoed()
    {
        var req = CreateRequest();
        req.Headers.Add("x-correlation-id", "sap-run-42");

        HttpResponseWriter.ResolveCorrelationId(req).Should().Be("sap-run-42");
    }

    [Fact]
    public void ResolveCorrelationId_MissingHeader_GeneratesId()
    {
        var req = CreateRequest();

        var id = HttpResponseWriter.ResolveCorrelationId(req);

        id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParseExact(id, "N", out _).Should().BeTrue();
    }

    [Fact]
    public void ResolveCorrelationId_TooLong_Replaced()
    {
        var req = CreateRequest();
        req.Headers.Add("x-correlation-id", new string('a', 65));

        var id = HttpResponseWriter.ResolveCorrelationId(req);

        id.Length.Should().BeLessThanOrEqualTo(64);
        Guid.TryParseExact(id, "N", out _).Should().BeTrue();
    }

    [Fact]
    public void ResolveCorrelationId_ControlCharacters_Replaced()
    {
        var req = CreateRequest();
        req.Headers.TryAddWithoutValidation("x-correlation-id", "bad\tvalue");

        var id = HttpResponseWriter.ResolveCorrelationId(req);

        Guid.TryParseExact(id, "N", out _).Should().BeTrue();
    }

    [Fact]
    public async Task WriteJsonAsync_SerializesEnumsAsStrings_PascalCase()
    {
        var req = CreateRequest();
        var results = new[]
        {
            new UpsertResult { EntityLogicalName = "account", ErrorCategory = ErrorCategory.Throttling }
        };

        var response = await HttpResponseWriter.WriteJsonAsync(req, HttpStatusCode.OK, results, "corr-1");

        var body = ((FakeHttpResponseData)response).ReadBody();
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement[0];
        first.GetProperty("ErrorCategory").GetString().Should().Be("Throttling");
        first.GetProperty("EntityLogicalName").GetString().Should().Be("account");
        response.Headers.TryGetValues("Content-Type", out var ct).Should().BeTrue();
        ct!.First().Should().StartWith("application/json");
    }

    [Fact]
    public async Task WriteJsonAsync_NeverSerializesInternalException()
    {
        var req = CreateRequest();
        var results = new[]
        {
            new UpsertResult
            {
                EntityLogicalName = "account",
                ErrorCategory = ErrorCategory.Permanent,
                Exception = new InvalidOperationException("internal secret detail")
            }
        };

        var response = await HttpResponseWriter.WriteJsonAsync(req, HttpStatusCode.OK, results, "corr-1");

        var body = ((FakeHttpResponseData)response).ReadBody();
        body.Should().NotContain("internal secret detail");
        body.Should().NotContain("Exception");
    }

    [Fact]
    public async Task WriteJsonAsync_RetryAfter_SetOnResponse()
    {
        var req = CreateRequest();

        var response = await HttpResponseWriter.WriteJsonAsync(
            req, HttpStatusCode.TooManyRequests, Array.Empty<UpsertResult>(), "corr-1", retryAfterSeconds: 180);

        response.Headers.TryGetValues("Retry-After", out var values).Should().BeTrue();
        values!.First().Should().Be("180");
    }

    [Fact]
    public async Task WriteErrorAsync_ProducesStructuredJsonWithCorrelation()
    {
        var req = CreateRequest();

        var response = await HttpResponseWriter.WriteErrorAsync(
            req, HttpStatusCode.BadRequest, "Validation failed.", "corr-9",
            new[] { "Contacts[0]: Email is required." });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = ((FakeHttpResponseData)response).ReadBody();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("Error").GetString().Should().Be("Validation failed.");
        doc.RootElement.GetProperty("CorrelationId").GetString().Should().Be("corr-9");
        doc.RootElement.GetProperty("Details")[0].GetString().Should().Contain("Email is required");
    }
}
