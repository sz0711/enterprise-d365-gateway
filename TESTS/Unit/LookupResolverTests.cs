using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class LookupResolverTests
{
    private readonly Mock<IEntityUpsertExecutor> _executorMock = new();
    private readonly Mock<ILogger<LookupResolver>> _loggerMock = new();
    private readonly DataverseOptions _options = new() { MaxLookupDepth = 3 };
    private readonly LookupResolver _sut;

    public LookupResolverTests()
    {
        _sut = new LookupResolver(
            _executorMock.Object,
            _loggerMock.Object,
            Options.Create(_options));
    }

    private static LookupDefinition MakeLookup(
        string entity = "contact",
        string? upsertKey = null,
        bool createIfNotExists = false,
        IDictionary<string, object?>? altKeys = null,
        IDictionary<string, object?>? createAttrs = null,
        IDictionary<string, LookupDefinition>? nestedLookups = null,
        int? maxDepth = null)
    {
        return new LookupDefinition
        {
            EntityLogicalName = entity,
            UpsertKey = upsertKey,
            CreateIfNotExists = createIfNotExists,
            AlternateKeyAttributes = altKeys ?? new Dictionary<string, object?> { ["email"] = "test@example.com" },
            CreateAttributes = createAttrs,
            NestedLookups = nestedLookups,
            MaxDepth = maxDepth
        };
    }

    [Fact]
    public async Task ResolveAsync_ExistingRecord_ReturnsReferenceAndTrace()
    {
        var existingId = Guid.NewGuid();
        var collection = new EntityCollection();
        collection.Entities.Add(new Entity("contact") { Id = existingId });
        _executorMock
            .Setup(e => e.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(collection);

        var lookup = MakeLookup();
        var visited = new HashSet<string>();

        var (reference, trace) = await _sut.ResolveAsync("primarycontactid", lookup, 0, 3, visited);

        reference.LogicalName.Should().Be("contact");
        reference.Id.Should().Be(existingId);
        trace.WasCreated.Should().BeFalse();
        trace.ResolvedId.Should().Be(existingId);
    }

    [Fact]
    public async Task ResolveAsync_DepthExceeded_Throws()
    {
        var lookup = MakeLookup();
        var visited = new HashSet<string>();

        var act = async () => await _sut.ResolveAsync("primarycontactid", lookup, 3, 3, visited);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded maximum depth*");
    }

    [Fact]
    public async Task ResolveAsync_CycleDetected_Throws()
    {
        // Pre-populate visited with the key that will be generated
        var visited = new HashSet<string> { "contact:" };

        var lookup = MakeLookup();

        var act = async () => await _sut.ResolveAsync("primarycontactid", lookup, 0, 3, visited);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cyclic lookup*");
    }

    [Fact]
    public async Task ResolveAsync_MultipleResults_Throws()
    {
        var collection = new EntityCollection();
        collection.Entities.Add(new Entity("contact") { Id = Guid.NewGuid() });
        collection.Entities.Add(new Entity("contact") { Id = Guid.NewGuid() });
        _executorMock
            .Setup(e => e.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(collection);

        var lookup = MakeLookup();
        var visited = new HashSet<string>();

        var act = async () => await _sut.ResolveAsync("primarycontactid", lookup, 0, 3, visited);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*multiple*");
    }

    [Fact]
    public async Task ResolveAsync_NotFound_CreateIfNotExistsFalse_Throws()
    {
        _executorMock
            .Setup(e => e.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var lookup = MakeLookup(createIfNotExists: false);
        var visited = new HashSet<string>();

        var act = async () => await _sut.ResolveAsync("primarycontactid", lookup, 0, 3, visited);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*CreateIfNotExists is false*");
    }

    [Fact]
    public async Task ResolveAsync_NotFound_CreateIfNotExistsTrue_CreatesAndReturns()
    {
        var newId = Guid.NewGuid();
        _executorMock
            .Setup(e => e.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());
        _executorMock
            .Setup(e => e.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newId);

        var lookup = MakeLookup(createIfNotExists: true);
        var visited = new HashSet<string>();

        var (reference, trace) = await _sut.ResolveAsync("primarycontactid", lookup, 0, 3, visited);

        reference.LogicalName.Should().Be("contact");
        reference.Id.Should().Be(newId);
        trace.WasCreated.Should().BeTrue();
        trace.ResolvedId.Should().Be(newId);
    }

    [Fact]
    public async Task ResolveAsync_CreateAttributes_MergedWithAlternateKeys()
    {
        var newId = Guid.NewGuid();
        Entity? capturedEntity = null;

        _executorMock
            .Setup(e => e.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());
        _executorMock
            .Setup(e => e.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(newId);

        var lookup = MakeLookup(
            createIfNotExists: true,
            altKeys: new Dictionary<string, object?> { ["email"] = "test@example.com" },
            createAttrs: new Dictionary<string, object?> { ["firstname"] = "Test", ["lastname"] = "User" });

        var visited = new HashSet<string>();
        await _sut.ResolveAsync("primarycontactid", lookup, 0, 3, visited);

        capturedEntity.Should().NotBeNull();
        capturedEntity!.Attributes.Should().ContainKey("email");
        capturedEntity.Attributes.Should().ContainKey("firstname");
        capturedEntity.Attributes.Should().ContainKey("lastname");
    }

    [Fact]
    public async Task ResolveAsync_VisitedSet_CleanedUpAfterResolution()
    {
        var existingId = Guid.NewGuid();
        var collection = new EntityCollection();
        collection.Entities.Add(new Entity("contact") { Id = existingId });
        _executorMock
            .Setup(e => e.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(collection);

        var lookup = MakeLookup();
        var visited = new HashSet<string>();

        await _sut.ResolveAsync("primarycontactid", lookup, 0, 3, visited);

        // The cycle key should be removed from visited after resolution (finally block)
        visited.Should().NotContain("contact:");
    }

    [Fact]
    public async Task ResolveAsync_LookupDefMaxDepth_OverridesGlobalMaxDepth()
    {
        // Global maxDepth = 3, LookupDefinition maxDepth = 1, currentDepth = 1
        // Effective depth = 1, so depth 1 >= 1 → throws
        var lookup = MakeLookup(maxDepth: 1);
        var visited = new HashSet<string>();

        var act = async () => await _sut.ResolveAsync("primarycontactid", lookup, 1, 3, visited);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded maximum depth*");
    }

    [Fact]
    public async Task ResolveAsync_NestedLookups_ResolvedBeforeCreate()
    {
        var parentId = Guid.NewGuid();
        var nestedId = Guid.NewGuid();
        var callOrder = new List<string>();

        _executorMock
            .Setup(e => e.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        _executorMock
            .Setup(e => e.CreateAsync(It.Is<Entity>(ent => ent.LogicalName == "account"), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((_, _) => callOrder.Add("create_account"))
            .ReturnsAsync(nestedId);

        _executorMock
            .Setup(e => e.CreateAsync(It.Is<Entity>(ent => ent.LogicalName == "contact"), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((_, _) => callOrder.Add("create_contact"))
            .ReturnsAsync(parentId);

        var nestedLookup = MakeLookup(entity: "account", createIfNotExists: true,
            altKeys: new Dictionary<string, object?> { ["accountnumber"] = "ACC-001" });

        var parentLookup = MakeLookup(
            entity: "contact",
            createIfNotExists: true,
            nestedLookups: new Dictionary<string, LookupDefinition> { ["parentcustomerid"] = nestedLookup });

        var visited = new HashSet<string>();
        var (reference, trace) = await _sut.ResolveAsync("primarycontactid", parentLookup, 0, 3, visited);

        // Nested account should be created before parent contact
        callOrder.Should().BeEquivalentTo(new[] { "create_account", "create_contact" },
            opts => opts.WithStrictOrdering());

        trace.NestedTraces.Should().ContainSingle()
            .Which.EntityLogicalName.Should().Be("account");
    }
}
