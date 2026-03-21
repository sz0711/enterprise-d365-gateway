using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class EntityMappingCacheTests : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly EntityMappingCache _sut;
    private const string Signature = "account:accountnumber=EXT-001";

    public EntityMappingCacheTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        var options = Options.Create(new DataverseOptions
        {
            CacheSlidingExpirationMinutes = 120,
            CacheAbsoluteExpirationMinutes = 360,
            CacheEntrySizeBytes = 128
        });
        _sut = new EntityMappingCache(_cache, options);
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsCachedGuid()
    {
        var id = Guid.NewGuid();
        await _sut.SetAsync("account", Signature, id);

        var result = await _sut.GetAsync("account", Signature);

        result.Should().Be(id);
    }

    [Fact]
    public async Task GetAsync_CacheMiss_ReturnsNull()
    {
        var result = await _sut.GetAsync("account", "account:accountnumber=NONEXISTENT");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Remove_SubsequentGetReturnsNull()
    {
        var id = Guid.NewGuid();
        await _sut.SetAsync("account", Signature, id);

        _sut.Remove("account", Signature);

        var result = await _sut.GetAsync("account", Signature);
        result.Should().BeNull();
    }

    [Fact]
    public async Task KeyFormat_CaseInsensitive()
    {
        var id = Guid.NewGuid();
        await _sut.SetAsync("Account", "ACCOUNT:ACCOUNTNUMBER=EXT-001", id);

        var result = await _sut.GetAsync("account", Signature.ToLowerInvariant());

        result.Should().Be(id);
    }

    [Fact]
    public async Task SetAsync_SizeSetOnEntry()
    {
        var id = Guid.NewGuid();
        // If size limit is exceeded, the entry might not be added.
        // With SizeLimit=1M and CacheEntrySizeBytes=128, this should work.
        await _sut.SetAsync("account", Signature, id);

        var result = await _sut.GetAsync("account", Signature);
        result.Should().Be(id);
    }

    [Fact]
    public async Task SetAsync_MultipleEntities_IndependentKeys()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await _sut.SetAsync("account", "account:accountnumber=EXT-001", id1);
        await _sut.SetAsync("contact", "contact:emailaddress1=test@test.com", id2);

        (await _sut.GetAsync("account", "account:accountnumber=EXT-001")).Should().Be(id1);
        (await _sut.GetAsync("contact", "contact:emailaddress1=test@test.com")).Should().Be(id2);
    }

    public void Dispose() => _cache.Dispose();
}
