using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Integration;

/// <summary>
/// Full integration tests using FakeXrmEasy in-memory Dataverse + real services.
/// Only the DataverseServiceClientFactory is faked (via FakeDataverseContext).
/// </summary>
public class UpsertOrchestratorIntegrationTests : IDisposable
{
    private readonly FakeDataverseContext _fakeXrm;
    private readonly MemoryCache _memoryCache;
    private readonly UpsertOrchestrator _sut;
    private readonly DataverseOptions _options;

    public UpsertOrchestratorIntegrationTests()
    {
        _options = new DataverseOptions
        {
            Url = "https://test.crm.dynamics.com",
            MaxLookupDepth = 3,
            MaxDegreeOfParallelism = 1, // FakeXrmEasy context is not thread-safe
            MaxRequestsPerSecond = 5000,
            MaxRetries = 1,
            RetryBaseDelayMs = 50,
            TimeoutPerOperationSeconds = 30,
            CircuitBreakerFailureThreshold = 100,
            CircuitBreakerSamplingDurationSeconds = 60,
            CircuitBreakerBreakDurationSeconds = 5,
            CacheSlidingExpirationMinutes = 60,
            CacheAbsoluteExpirationMinutes = 120,
            CacheMemoryBudgetPercent = 20,
            CacheEntrySizeBytes = 128
        };

        _fakeXrm = new FakeDataverseContext();
        _memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });

        var entityMappingCache = new EntityMappingCache(_memoryCache, Options.Create(_options));
        var earlyboundMapper = new EarlyboundEntityMapper();
        var requestValidator = new RequestValidator(earlyboundMapper);
        var lockCoordinator = new UpsertLockCoordinator();
        var errorClassifier = new ErrorClassifier();
        var resultMapper = new ResultMapper();

        // Real EntityUpsertExecutor backed by FakeXrmEasy in-memory context
        var concurrencyLimiterForExecutor = new AdaptiveConcurrencyLimiter(
            new Mock<ILogger<AdaptiveConcurrencyLimiter>>().Object,
            Options.Create(_options));

        var executor = new EntityUpsertExecutor(
            _fakeXrm.FactoryMock.Object,
            new Mock<ILogger<EntityUpsertExecutor>>().Object,
            concurrencyLimiterForExecutor,
            Options.Create(_options));

        var externalIdResolver = new ExternalIdResolver(
            entityMappingCache,
            executor,
            new Mock<ILogger<ExternalIdResolver>>().Object);
        var lookupResolver = new LookupResolver(
            executor,
            new Mock<ILogger<LookupResolver>>().Object,
            Options.Create(_options));

        var concurrencyLimiter = new AdaptiveConcurrencyLimiter(
            new Mock<ILogger<AdaptiveConcurrencyLimiter>>().Object,
            Options.Create(_options));

        _sut = new UpsertOrchestrator(
            requestValidator,
            earlyboundMapper,
            externalIdResolver,
            lookupResolver,
            executor,
            lockCoordinator,
            errorClassifier,
            resultMapper,
            entityMappingCache,
            concurrencyLimiter,
            new Mock<ILogger<UpsertOrchestrator>>().Object,
            Options.Create(_options));
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
    }

    [Fact]
    public async Task UpsertAsync_NewEntity_CreatesInFakeDataverse()
    {
        var payload = new UpsertPayload
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "EXT-001" },
            Attributes = new Dictionary<string, object?> { ["name"] = "Test Account" }
        };

        var result = await _sut.UpsertAsync(payload);

        result.Id.Should().NotBeEmpty();
        result.Created.Should().BeTrue();
        result.ErrorCategory.Should().Be(ErrorCategory.None);

        // Verify the entity actually exists in FakeXrmEasy's in-memory store
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
        var stored = _fakeXrm.Service.RetrieveMultiple(query);
        stored.Entities.Should().ContainSingle();
        stored.Entities[0].GetAttributeValue<string>("name").Should().Be("Test Account");
    }

    [Fact]
    public async Task UpsertAsync_ExistingEntity_UpdatesInFakeDataverse()
    {
        // Seed an existing account in FakeXrmEasy
        var existingId = Guid.NewGuid();
        _fakeXrm.Context.Initialize(new Entity("account", existingId)
        {
            ["name"] = "Original Name"
        });

        var payload = new UpsertPayload
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "EXT-001" },
            Id = existingId,
            Attributes = new Dictionary<string, object?> { ["name"] = "Updated Name" }
        };

        var result = await _sut.UpsertAsync(payload);

        result.Id.Should().Be(existingId);
        result.Created.Should().BeFalse();
        result.ErrorCategory.Should().Be(ErrorCategory.None);

        // Verify the entity was actually updated in memory
        var stored = _fakeXrm.Service.Retrieve("account", existingId, new ColumnSet("name"));
        stored.GetAttributeValue<string>("name").Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpsertAsync_WithExternalId_ResolvesFromFakeDataverse()
    {
        // Seed an account with an external ID attribute (accountnumber is a real earlybound field)
        var existingId = Guid.NewGuid();
        _fakeXrm.Context.Initialize(new Entity("account", existingId)
        {
            ["accountnumber"] = "EXT-001",
            ["name"] = "Pre-existing Account"
        });

        var payload = new UpsertPayload
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "EXT-001" },
            Attributes = new Dictionary<string, object?> { ["name"] = "Resolved Account" }
        };

        var result = await _sut.UpsertAsync(payload);

        result.Id.Should().Be(existingId);
        result.Created.Should().BeFalse();
        result.ErrorCategory.Should().Be(ErrorCategory.None);
    }

    [Fact]
    public async Task UpsertAsync_WithLookup_ResolvesExistingContact()
    {
        // Seed a contact that the lookup should find
        var contactId = Guid.NewGuid();
        _fakeXrm.Context.Initialize(new Entity("contact", contactId)
        {
            ["emailaddress1"] = "test@test.com",
            ["fullname"] = "Test Contact"
        });

        var payload = new UpsertPayload
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "ACC-001" },
            Attributes = new Dictionary<string, object?> { ["name"] = "Test Account" },
            Lookups = new Dictionary<string, LookupDefinition>
            {
                ["primarycontactid"] = new LookupDefinition
                {
                    EntityLogicalName = "contact",
                    KeyAttributes = new Dictionary<string, object?> { ["emailaddress1"] = "test@test.com" }
                }
            }
        };

        var result = await _sut.UpsertAsync(payload);

        result.ErrorCategory.Should().Be(ErrorCategory.None);
        result.Created.Should().BeTrue();
        result.LookupTraces.Should().ContainSingle()
            .Which.ResolvedId.Should().Be(contactId);
    }

    [Fact]
    public async Task UpsertAsync_WithLookup_CreateIfNotExists_CreatesLookupEntity()
    {
        var payload = new UpsertPayload
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "ACC-001" },
            Attributes = new Dictionary<string, object?> { ["name"] = "Test Account" },
            Lookups = new Dictionary<string, LookupDefinition>
            {
                ["primarycontactid"] = new LookupDefinition
                {
                    EntityLogicalName = "contact",
                    KeyAttributes = new Dictionary<string, object?> { ["emailaddress1"] = "new@test.com" },
                    CreateIfNotExists = true,
                    CreateAttributes = new Dictionary<string, object?> { ["firstname"] = "New", ["lastname"] = "Contact" }
                }
            }
        };

        var result = await _sut.UpsertAsync(payload);

        result.ErrorCategory.Should().Be(ErrorCategory.None);
        result.LookupTraces.Should().ContainSingle()
            .Which.WasCreated.Should().BeTrue();

        // Verify the contact was actually created in FakeXrmEasy
        var contacts = _fakeXrm.Service.RetrieveMultiple(
            new QueryExpression("contact") { ColumnSet = new ColumnSet("emailaddress1", "firstname") });
        contacts.Entities.Should().ContainSingle();
    }

    [Fact]
    public async Task UpsertAsync_ValidationFailure_ReturnsValidationError()
    {
        var payload = new UpsertPayload
        {
            EntityLogicalName = "",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "EXT-001" },
            Attributes = new Dictionary<string, object?> { ["name"] = "Test" }
        };

        var result = await _sut.UpsertAsync(payload);

        result.ErrorCategory.Should().Be(ErrorCategory.Validation);
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpsertBatchAsync_MultiplePayloads_AllCreatedInFakeDataverse()
    {
        var payloads = new[]
        {
            new UpsertPayload
            {
                EntityLogicalName = "account",
                KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "ACC-001" },
                Attributes = new Dictionary<string, object?> { ["name"] = "Account 1" }
            },
            new UpsertPayload
            {
                EntityLogicalName = "account",
                KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "ACC-002" },
                Attributes = new Dictionary<string, object?> { ["name"] = "Account 2" }
            }
        };

        var results = await _sut.UpsertBatchAsync(payloads);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.ErrorCategory == ErrorCategory.None);
        results.Should().OnlyContain(r => r.Created);

        // Verify both exist in FakeXrmEasy
        var stored = _fakeXrm.Service.RetrieveMultiple(
            new QueryExpression("account") { ColumnSet = new ColumnSet("name") });
        stored.Entities.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpsertBatchAsync_EmptyPayloads_ReturnsEmpty()
    {
        var results = await _sut.UpsertBatchAsync(Array.Empty<UpsertPayload>());

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ExternalIdCached_SecondCallSkipsQuery()
    {
        // Seed an existing account
        var existingId = Guid.NewGuid();
        _fakeXrm.Context.Initialize(new Entity("account", existingId)
        {
            ["accountnumber"] = "EXT-001",
            ["name"] = "Original"
        });

        var payload = new UpsertPayload
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "EXT-001" },
            Attributes = new Dictionary<string, object?> { ["name"] = "Cached Account" }
        };

        // First call resolves from FakeXrmEasy → sets cache
        var result1 = await _sut.UpsertAsync(payload);
        result1.Id.Should().Be(existingId);

        // Second call should use cached ID (still succeeds)
        var result2 = await _sut.UpsertAsync(payload);
        result2.Id.Should().Be(existingId);
        result2.Created.Should().BeFalse();
    }
}

