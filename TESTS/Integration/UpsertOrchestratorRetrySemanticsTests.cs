using System.ServiceModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Integration;

/// <summary>
/// Invalidate-and-retry semantics: exactly key-conflict failures (stale cached
/// GUID, create race) trigger a single retry with invalidated cache; every
/// other failure fails fast without a second full upsert cycle.
/// </summary>
public class UpsertOrchestratorRetrySemanticsTests
{
    private readonly Mock<IRequestValidator> _validatorMock = new();
    private readonly Mock<IEarlyboundEntityMapper> _mapperMock = new();
    private readonly Mock<IExternalIdResolver> _externalIdResolverMock = new();
    private readonly Mock<ILookupResolver> _lookupResolverMock = new();
    private readonly Mock<IEntityUpsertExecutor> _executorMock = new();
    private readonly Mock<IUpsertLockCoordinator> _lockMock = new();
    private readonly Mock<IEntityMappingCache> _cacheMock = new();
    private readonly Mock<IAdaptiveConcurrencyLimiter> _limiterMock = new();
    private readonly UpsertOrchestrator _sut;

    public UpsertOrchestratorRetrySemanticsTests()
    {
        _mapperMock
            .Setup(m => m.MapToEntity(It.IsAny<UpsertPayload>()))
            .Returns(() => new Entity("account"));
        _lockMock
            .Setup(l => l.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IDisposable>().Object);
        _externalIdResolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid?)null);
        _limiterMock.SetupGet(l => l.CurrentLimit).Returns(1);

        _sut = new UpsertOrchestrator(
            _validatorMock.Object,
            _mapperMock.Object,
            _externalIdResolverMock.Object,
            _lookupResolverMock.Object,
            _executorMock.Object,
            _lockMock.Object,
            new ErrorClassifier(),
            new ResultMapper(),
            _cacheMock.Object,
            _limiterMock.Object,
            new Mock<ILogger<UpsertOrchestrator>>().Object,
            Options.Create(new DataverseOptions()));
    }

    private static UpsertPayload Payload() => new()
    {
        EntityLogicalName = "account",
        KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "RETRY-1" },
        Attributes = new Dictionary<string, object?> { ["name"] = "Retry Corp" }
    };

    private static FaultException<OrganizationServiceFault> DuplicateKeyFault()
        => new(new OrganizationServiceFault
        {
            ErrorCode = DataverseErrorCodes.DuplicateAlternateKey,
            Message = "A record that has the attribute values already exists."
        }, "duplicate");

    [Fact]
    public async Task KeyConflictFailure_InvalidatesCacheAndRetriesOnce()
    {
        var createdId = Guid.NewGuid();
        var calls = 0;
        _executorMock
            .Setup(e => e.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                if (calls == 1) throw DuplicateKeyFault();
                return createdId;
            });

        var result = await _sut.UpsertAsync(Payload());

        calls.Should().Be(2);
        result.ErrorCategory.Should().Be(ErrorCategory.None);
        result.Id.Should().Be(createdId);
        _externalIdResolverMock.Verify(
            r => r.Invalidate("account", It.IsAny<IDictionary<string, object?>>()),
            Times.Once);
    }

    [Fact]
    public async Task NonKeyConflictPermanentFailure_NoRetry()
    {
        var calls = 0;
        _executorMock
            .Setup(e => e.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                throw new FaultException<OrganizationServiceFault>(
                    new OrganizationServiceFault { ErrorCode = -2147220960, Message = "Missing privilege." },
                    "privilege");
            });

        var result = await _sut.UpsertAsync(Payload());

        calls.Should().Be(1, "a genuine permanent error must not double the Dataverse work");
        result.ErrorCategory.Should().Be(ErrorCategory.Permanent);
        _externalIdResolverMock.Verify(
            r => r.Invalidate(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>()),
            Times.Never);
    }

    [Fact]
    public async Task TransientFailure_NoOrchestratorRetry()
    {
        // The executor's Polly pipeline already retried with backoff — a second
        // full cycle at the orchestrator would multiply the load.
        var calls = 0;
        _executorMock
            .Setup(e => e.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                throw new TimeoutException("channel timed out");
            });

        var result = await _sut.UpsertAsync(Payload());

        calls.Should().Be(1);
        result.ErrorCategory.Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public async Task FailureResult_NeverExposesInternalException()
    {
        _executorMock
            .Setup(e => e.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sensitive"));

        var result = await _sut.UpsertAsync(Payload());

        result.Exception.Should().BeNull("the exception is orchestrator-internal and must never leave it");
    }

    [Fact]
    public async Task NullPayloadInBatch_YieldsValidationResult_NotCrash()
    {
        _validatorMock
            .Setup(v => v.Validate(null!))
            .Throws(new PayloadValidationException(new[] { "Payload must not be null." }));

        var results = await _sut.UpsertBatchAsync(new UpsertPayload?[] { null }!);

        results.Should().ContainSingle()
            .Which.ErrorCategory.Should().Be(ErrorCategory.Validation);
    }
}
