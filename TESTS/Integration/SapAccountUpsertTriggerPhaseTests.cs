using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Moq;
using enterprise_d365_gateway.Functions;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Integration;

/// <summary>
/// Three-phase semantics of the SAP endpoint: phase-1 short-circuit, direct
/// GUID wiring (no redundant lookups), phase-3 skip on failed primary contact,
/// contact fan-out limit and the serialized response contract.
/// </summary>
public class SapAccountUpsertTriggerPhaseTests
{
    private readonly Mock<IDataverseUpsertService> _upsertServiceMock = new();
    private readonly Mock<FunctionContext> _contextMock = new();
    private readonly DataverseOptions _options = new();
    private readonly SapAccountUpsertTrigger _sut;

    public SapAccountUpsertTriggerPhaseTests()
    {
        _contextMock.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        _sut = new SapAccountUpsertTrigger(
            new SapAccountMapper(),
            _upsertServiceMock.Object,
            new ResultMapper(),
            new Mock<ILogger<SapAccountUpsertTrigger>>().Object,
            Options.Create(_options));
    }

    private FakeHttpRequestData CreateRequest(string body) => new(_contextMock.Object, body);

    private const string RequestWithContacts = """
    {
        "accountNumber": "SAP-77",
        "name": "Phase Corp",
        "contacts": [
            { "email": "primary@example.com", "firstName": "Jane", "lastName": "Prime", "isPrimary": true },
            { "email": "second@example.com", "firstName": "Bob", "lastName": "Second" }
        ]
    }
    """;

    [Fact]
    public async Task Phase1Failure_ShortCircuits_NoContactUpserts()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { ErrorCategory = ErrorCategory.Permanent, ErrorMessage = "boom" });

        var response = await _sut.RunAsync(CreateRequest(RequestWithContacts));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        _upsertServiceMock.Verify(
            s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "contacts must not be attempted when the account failed");

        var body = ((FakeHttpResponseData)response).ReadBody();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetArrayLength().Should().Be(1, "only the account result is returned");
    }

    [Fact]
    public async Task Phase1ThrottledOnly_Returns429WithRetryAfter()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { ErrorCategory = ErrorCategory.Throttling, ErrorMessage = "throttled" });

        var response = await _sut.RunAsync(CreateRequest(RequestWithContacts));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.TryGetValues("Retry-After", out var retryAfter).Should().BeTrue();
        int.Parse(retryAfter!.First()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Phase2_AccountGuidInjected_NoLookupRoundTrips()
    {
        var accountId = Guid.NewGuid();
        IReadOnlyList<UpsertPayload>? capturedContacts = null;

        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { Id = accountId, Created = true, ErrorCategory = ErrorCategory.None });
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<UpsertPayload>, CancellationToken>((p, _) => capturedContacts = p.ToList())
            .ReturnsAsync(new List<UpsertResult>
            {
                new() { Id = Guid.NewGuid(), ErrorCategory = ErrorCategory.None },
                new() { Id = Guid.NewGuid(), ErrorCategory = ErrorCategory.None }
            });

        await _sut.RunAsync(CreateRequest(RequestWithContacts));

        capturedContacts.Should().NotBeNull();
        foreach (var contact in capturedContacts!)
        {
            contact.Lookups.Should().BeNull("the account GUID is known — no lookup round trip needed");
            contact.Attributes["parentcustomerid"].Should().BeOfType<EntityReference>()
                .Which.Id.Should().Be(accountId);
        }
    }

    [Fact]
    public async Task Phase3_PrimaryContactGuidInjected_DirectLink()
    {
        var accountId = Guid.NewGuid();
        var primaryContactId = Guid.NewGuid();
        var linkPayloads = new List<UpsertPayload>();

        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertPayload, CancellationToken>((p, _) => linkPayloads.Add(p))
            .ReturnsAsync(new UpsertResult { Id = accountId, Created = false, ErrorCategory = ErrorCategory.None });
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpsertResult>
            {
                new() { Id = primaryContactId, ErrorCategory = ErrorCategory.None },
                new() { Id = Guid.NewGuid(), ErrorCategory = ErrorCategory.None }
            });

        var response = await _sut.RunAsync(CreateRequest(RequestWithContacts));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        linkPayloads.Should().HaveCount(2, "account upsert + primary contact link");

        var link = linkPayloads[1];
        link.Id.Should().Be(accountId, "the account GUID from phase 1 skips key resolution");
        link.Lookups.Should().BeNull("the contact GUID is known — no e-mail lookup needed");
        link.Attributes["primarycontactid"].Should().BeOfType<EntityReference>()
            .Which.Id.Should().Be(primaryContactId);
    }

    [Fact]
    public async Task Phase3_PrimaryContactFailed_LinkSkippedWithExplicitResult()
    {
        var accountId = Guid.NewGuid();

        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { Id = accountId, ErrorCategory = ErrorCategory.None });
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpsertResult>
            {
                new() { ErrorCategory = ErrorCategory.Permanent, ErrorMessage = "contact failed" },
                new() { Id = Guid.NewGuid(), ErrorCategory = ErrorCategory.None }
            });

        var response = await _sut.RunAsync(CreateRequest(RequestWithContacts));

        // Account upsert once, but NO second UpsertAsync for the link.
        _upsertServiceMock.Verify(
            s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var body = ((FakeHttpResponseData)response).ReadBody();
        body.Should().Contain("Primary contact link skipped");
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Phase3_PrimaryContactThrottled_Returns429NotManufactured500()
    {
        var accountId = Guid.NewGuid();

        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { Id = accountId, ErrorCategory = ErrorCategory.None });
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpsertResult>
            {
                new() { ErrorCategory = ErrorCategory.Throttling, ErrorMessage = "throttled" },
                new() { Id = Guid.NewGuid(), ErrorCategory = ErrorCategory.None }
            });

        var response = await _sut.RunAsync(CreateRequest(RequestWithContacts));

        // The synthetic skip marker must carry the contact's Throttling category so
        // the batch surfaces as retryable 429, not a manufactured 500.
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.TryGetValues("Retry-After", out _).Should().BeTrue();

        var body = ((FakeHttpResponseData)response).ReadBody();
        body.Should().Contain("Primary contact link skipped");
    }

    [Fact]
    public async Task ContactsOverLimit_Returns400()
    {
        _options.MaxBatchItems = 1;

        var response = await _sut.RunAsync(CreateRequest(RequestWithContacts));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = ((FakeHttpResponseData)response).ReadBody();
        body.Should().Contain("exceeds the maximum");
        _upsertServiceMock.Verify(
            s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvalidContacts_Returns400WithDetails()
    {
        var json = """
        {
            "accountNumber": "SAP-77",
            "name": "Phase Corp",
            "contacts": [ { "email": "", "firstName": "", "lastName": "" } ]
        }
        """;

        var response = await _sut.RunAsync(CreateRequest(json));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = ((FakeHttpResponseData)response).ReadBody();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("Error").GetString().Should().Be("Validation failed.");
        doc.RootElement.GetProperty("Details").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Response_SerializesErrorCategoryAsString()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { Id = Guid.NewGuid(), Created = true, ErrorCategory = ErrorCategory.None });

        var response = await _sut.RunAsync(CreateRequest("""{"accountNumber":"SAP-1","name":"Contract Corp"}"""));

        var body = ((FakeHttpResponseData)response).ReadBody();
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement[0];
        first.GetProperty("ErrorCategory").ValueKind.Should().Be(JsonValueKind.String);
        first.GetProperty("ErrorCategory").GetString().Should().Be("None");
        first.GetProperty("Created").GetBoolean().Should().BeTrue();

        response.Headers.TryGetValues("Content-Type", out var contentType).Should().BeTrue();
        contentType!.First().Should().StartWith("application/json");
    }
}
