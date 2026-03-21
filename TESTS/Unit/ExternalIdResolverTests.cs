using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class ExternalIdResolverTests
{
    private readonly Mock<IEntityMappingCache> _cacheMock = new();
    private readonly Mock<IEntityUpsertExecutor> _executorMock = new();
    private readonly Mock<ILogger<ExternalIdResolver>> _loggerMock = new();
    private readonly ExternalIdResolver _sut;

    public ExternalIdResolverTests()
    {
        _sut = new ExternalIdResolver(_cacheMock.Object, _executorMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ResolveAsync_CacheHit_ReturnsCachedGuidNoQuery()
    {
        var expectedId = Guid.NewGuid();
        _cacheMock.Setup(c => c.GetAsync("account", "account:ext_id=EXT-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var keyAttributes = new Dictionary<string, object?> { ["ext_id"] = "EXT-001" };

        var result = await _sut.ResolveAsync("account", keyAttributes);

        result.Should().Be(expectedId);
        _executorMock.Verify(e => e.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_CacheMiss_QueryReturnsOne_CachesAndReturns()
    {
        var expectedId = Guid.NewGuid();
        _cacheMock.Setup(c => c.GetAsync("account", "account:ext_id=EXT-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var keyAttributes = new Dictionary<string, object?> { ["ext_id"] = "EXT-001" };

        var collection = new EntityCollection();
        collection.Entities.Add(new Entity("account") { Id = expectedId });
        _executorMock.Setup(e => e.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(collection);

        var result = await _sut.ResolveAsync("account", keyAttributes);

        result.Should().Be(expectedId);
        _cacheMock.Verify(c => c.SetAsync("account", "account:ext_id=EXT-001", expectedId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_CacheMiss_QueryReturnsZero_ReturnsNull()
    {
        _cacheMock.Setup(c => c.GetAsync("account", "account:ext_id=EXT-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        _executorMock.Setup(e => e.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var keyAttributes = new Dictionary<string, object?> { ["ext_id"] = "EXT-001" };

        var result = await _sut.ResolveAsync("account", keyAttributes);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_CacheMiss_QueryReturnsMultiple_Throws()
    {
        _cacheMock.Setup(c => c.GetAsync("account", "account:ext_id=EXT-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var keyAttributes = new Dictionary<string, object?> { ["ext_id"] = "EXT-001" };

        var collection = new EntityCollection();
        collection.Entities.Add(new Entity("account") { Id = Guid.NewGuid() });
        collection.Entities.Add(new Entity("account") { Id = Guid.NewGuid() });
        _executorMock.Setup(e => e.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(collection);

        var act = async () => await _sut.ResolveAsync("account", keyAttributes);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Multiple*");
    }

    [Fact]
    public async Task ResolveAsync_EmptyKeyAttributes_ReturnsNull()
    {
        var result = await _sut.ResolveAsync("account", new Dictionary<string, object?>());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_NullKeyAttributes_ReturnsNull()
    {
        var result = await _sut.ResolveAsync("account", null!);

        result.Should().BeNull();
    }

    [Fact]
    public void Invalidate_CallsCacheRemove()
    {
        _sut.Invalidate("account", new Dictionary<string, object?> { ["ext_id"] = "EXT-001" });

        _cacheMock.Verify(c => c.Remove("account", "account:ext_id=EXT-001"), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_CancellationTokenPassedThrough()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _cacheMock.Setup(c => c.GetAsync("account", "account:ext_id=EXT-001", token))
            .ReturnsAsync((Guid?)null);
        _executorMock.Setup(e => e.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>(), token))
            .ReturnsAsync(new EntityCollection());

        await _sut.ResolveAsync("account", new Dictionary<string, object?> { ["ext_id"] = "EXT-001" }, token);

        _executorMock.Verify(e => e.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>(), token), Times.Once);
    }
}
