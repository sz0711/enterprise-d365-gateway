using System.Net;
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

public class SapAccountUpsertTriggerIntegrationTests
{
    private readonly Mock<IDataverseUpsertService> _upsertServiceMock = new();
    private readonly Mock<IResultMapper> _resultMapperMock = new();
    private readonly Mock<ILogger<SapAccountUpsertTrigger>> _loggerMock = new();
    private readonly Mock<FunctionContext> _contextMock = new();
    private readonly ISapAccountMapper _mapper = new SapAccountMapper();
    private readonly DataverseOptions _options;
    private readonly SapAccountUpsertTrigger _sut;

    public SapAccountUpsertTriggerIntegrationTests()
    {
        _contextMock.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        _options = new DataverseOptions();
        _sut = new SapAccountUpsertTrigger(
            _mapper,
            _upsertServiceMock.Object,
            _resultMapperMock.Object,
            _loggerMock.Object,
            Options.Create(_options));

        // Default mock: every single upsert succeeds
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { Id = Guid.NewGuid(), Created = true, ErrorCategory = ErrorCategory.None });
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpsertResult>());
        _resultMapperMock
            .Setup(m => m.DetermineBatchStatusCode(It.IsAny<IReadOnlyList<UpsertResult>>()))
            .Returns(HttpStatusCode.OK);
    }

    private FakeHttpRequestData CreateRequest(string body)
    {
        return new FakeHttpRequestData(_contextMock.Object, body);
    }

    [Fact]
    public async Task RunAsync_ValidSapPayload_ReturnsResults()
    {
        var json = """{"accountNumber":"SAP-001","name":"Contoso"}""";
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().Contain(h => h.Key == "x-correlation-id");
        _upsertServiceMock.Verify(s => s.UpsertAsync(
            It.Is<UpsertPayload>(p => p.EntityLogicalName == "account"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WithContacts_ThreePhaseExecution()
    {
        var capturedAccount = (UpsertPayload?)null;
        var capturedContacts = (IEnumerable<UpsertPayload>?)null;
        var capturedLink = (UpsertPayload?)null;
        var upsertCallCount = 0;

        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertPayload, CancellationToken>((p, _) =>
            {
                upsertCallCount++;
                if (upsertCallCount == 1) capturedAccount = p;
                else capturedLink = p;
            })
            .ReturnsAsync(new UpsertResult { ErrorCategory = ErrorCategory.None });
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<UpsertPayload>, CancellationToken>((p, _) => capturedContacts = p)
            .ReturnsAsync(new List<UpsertResult> { new() { ErrorCategory = ErrorCategory.None } });

        var json = """
        {
            "accountNumber": "SAP-002",
            "name": "Adventure Works",
            "contacts": [
                { "email": "john@example.com", "firstName": "John", "lastName": "Doe", "isPrimary": true }
            ]
        }
        """;
        var req = CreateRequest(json);

        await _sut.RunAsync(req);

        // Phase 1: account upserted first
        capturedAccount.Should().NotBeNull();
        capturedAccount!.EntityLogicalName.Should().Be("account");
        capturedAccount.Lookups.Should().BeNull();

        // Phase 2: contacts batch
        capturedContacts.Should().NotBeNull();
        var contactList = capturedContacts!.ToList();
        contactList.Should().HaveCount(1);
        contactList[0].EntityLogicalName.Should().Be("contact");
        contactList[0].Lookups.Should().ContainKey("parentcustomerid");

        // Phase 3: primary contact link
        capturedLink.Should().NotBeNull();
        capturedLink!.EntityLogicalName.Should().Be("account");
        capturedLink.Lookups.Should().ContainKey("primarycontactid");
    }

    [Fact]
    public async Task RunAsync_InvalidJson_Returns400()
    {
        var req = CreateRequest("not valid json {{{");

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunAsync_EmptyAccountNumber_Returns400()
    {
        var json = """{"accountNumber":"","name":"Test"}""";
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunAsync_ServiceThrows_Returns500()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        var json = """{"accountNumber":"SAP-001","name":"Contoso"}""";
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task RunAsync_CorrelationIdFromHeader_PreservedInResponse()
    {
        var json = """{"accountNumber":"SAP-001","name":"Contoso"}""";
        var req = CreateRequest(json);
        req.Headers.Add("x-correlation-id", "sap-corr-456");

        var response = await _sut.RunAsync(req);

        response.Headers.TryGetValues("x-correlation-id", out var values).Should().BeTrue();
        values!.First().Should().Be("sap-corr-456");
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_Returns408()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var json = """{"accountNumber":"SAP-001","name":"Contoso"}""";
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
    }

    [Fact]
    public async Task RunAsync_MissingName_Returns400()
    {
        var json = """{"accountNumber":"SAP-001","name":""}""";
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunAsync_MultipleContacts_AllSentInBatch()
    {
        IEnumerable<UpsertPayload>? capturedContacts = null;
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<UpsertPayload>, CancellationToken>((p, _) => capturedContacts = p)
            .ReturnsAsync(new List<UpsertResult>
            {
                new() { ErrorCategory = ErrorCategory.None },
                new() { ErrorCategory = ErrorCategory.None },
                new() { ErrorCategory = ErrorCategory.None }
            });

        var json = """
        {
            "accountNumber": "SAP-100",
            "name": "Multi-Contact Corp",
            "contacts": [
                { "email": "a@example.com", "firstName": "Alice", "lastName": "A" },
                { "email": "b@example.com", "firstName": "Bob", "lastName": "B" },
                { "email": "c@example.com", "firstName": "Charlie", "lastName": "C" }
            ]
        }
        """;
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capturedContacts.Should().NotBeNull();
        var contactList = capturedContacts!.ToList();
        contactList.Should().HaveCount(3);
        contactList.Should().OnlyContain(p => p.EntityLogicalName == "contact");
    }

    [Fact]
    public async Task RunAsync_BodyTooLarge_Returns413()
    {
        _options.MaxRequestBytes = 50;

        var sut = new SapAccountUpsertTrigger(
            _mapper,
            _upsertServiceMock.Object,
            _resultMapperMock.Object,
            _loggerMock.Object,
            Options.Create(_options));

        var json = """{"accountNumber":"SAP-001","name":"This name is long enough to exceed the very small limit set above"}""";
        var req = CreateRequest(json);

        var response = await sut.RunAsync(req);

        ((int)response.StatusCode).Should().Be(413);
    }

    [Fact]
    public async Task RunAsync_PartialFailure_UsesResultMapperStatusCode()
    {
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpsertResult { ErrorCategory = ErrorCategory.None });
        _upsertServiceMock
            .Setup(s => s.UpsertBatchAsync(It.IsAny<IEnumerable<UpsertPayload>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpsertResult>
            {
                new() { ErrorCategory = ErrorCategory.Permanent, ErrorMessage = "Duplicate key" }
            });
        _resultMapperMock
            .Setup(m => m.DetermineBatchStatusCode(It.IsAny<IReadOnlyList<UpsertResult>>()))
            .Returns(HttpStatusCode.InternalServerError);

        var json = """
        {
            "accountNumber": "SAP-PARTIAL",
            "name": "Partial Fail Corp",
            "contacts": [
                { "email": "z@example.com", "firstName": "Zara", "lastName": "Z" }
            ]
        }
        """;
        var req = CreateRequest(json);

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.Should().Contain(h => h.Key == "x-correlation-id");
    }

    [Fact]
    public async Task RunAsync_NullBody_Returns400()
    {
        var req = CreateRequest("");

        var response = await _sut.RunAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunAsync_WithOptionalAccountFields_MappedToDataverseAttributes()
    {
        UpsertPayload? capturedAccount = null;
        _upsertServiceMock
            .Setup(s => s.UpsertAsync(It.IsAny<UpsertPayload>(), It.IsAny<CancellationToken>()))
            .Callback<UpsertPayload, CancellationToken>((p, _) => capturedAccount = p)
            .ReturnsAsync(new UpsertResult { ErrorCategory = ErrorCategory.None });

        var json = """
        {
            "accountNumber": "SAP-FULL",
            "name": "Full Corp",
            "city": "Berlin",
            "street": "Alexanderplatz 1",
            "postalCode": "10178",
            "country": "DE",
            "phone": "+49 30 123",
            "email": "info@full.de",
            "website": "https://full.de"
        }
        """;
        var req = CreateRequest(json);

        await _sut.RunAsync(req);

        capturedAccount.Should().NotBeNull();
        capturedAccount!.Attributes["address1_city"].Should().Be("Berlin");
        capturedAccount.Attributes["address1_line1"].Should().Be("Alexanderplatz 1");
        capturedAccount.Attributes["address1_postalcode"].Should().Be("10178");
        capturedAccount.Attributes["address1_country"].Should().Be("DE");
        capturedAccount.Attributes["telephone1"].Should().Be("+49 30 123");
        capturedAccount.Attributes["emailaddress1"].Should().Be("info@full.de");
        capturedAccount.Attributes["websiteurl"].Should().Be("https://full.de");
    }
}
