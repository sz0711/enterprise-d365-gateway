using FluentAssertions;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

public class DataverseValueNormalizerTests
{
    [Fact]
    public void Normalize_Null_ReturnsNull()
    {
        DataverseValueNormalizer.Normalize(null).Should().BeNull();
    }

    [Fact]
    public void Normalize_NonJsonElement_PassesThrough()
    {
        DataverseValueNormalizer.Normalize("hello").Should().Be("hello");
        DataverseValueNormalizer.Normalize(42).Should().Be(42);
    }

    [Fact]
    public void Normalize_JsonNull_ReturnsNull()
    {
        var element = JsonElementFactory.FromNull();
        DataverseValueNormalizer.Normalize(element).Should().BeNull();
    }

    [Fact]
    public void Normalize_JsonTrue_ReturnsBool()
    {
        var element = JsonElementFactory.From(true);
        DataverseValueNormalizer.Normalize(element).Should().Be(true);
    }

    [Fact]
    public void Normalize_JsonFalse_ReturnsBool()
    {
        var element = JsonElementFactory.From(false);
        DataverseValueNormalizer.Normalize(element).Should().Be(false);
    }

    [Fact]
    public void Normalize_JsonStringGuid_ReturnsGuid()
    {
        var guid = Guid.NewGuid();
        var element = JsonElementFactory.From(guid);
        DataverseValueNormalizer.Normalize(element).Should().Be(guid);
    }

    [Fact]
    public void Normalize_JsonStringDateTime_ReturnsDateTimeUtc()
    {
        var dt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var element = JsonElementFactory.From(dt);
        var result = DataverseValueNormalizer.Normalize(element);
        result.Should().BeOfType<DateTime>();
        ((DateTime)result!).Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Normalize_JsonStringPlain_ReturnsString()
    {
        var element = JsonElementFactory.From("hello world");
        DataverseValueNormalizer.Normalize(element).Should().Be("hello world");
    }

    [Fact]
    public void Normalize_JsonNumberInt_ReturnsInt()
    {
        var element = JsonElementFactory.From(42);
        DataverseValueNormalizer.Normalize(element).Should().BeOfType<int>().And.Be(42);
    }

    [Fact]
    public void Normalize_JsonNumberLong_ReturnsLong()
    {
        var element = JsonElementFactory.From(3_000_000_000L);
        DataverseValueNormalizer.Normalize(element).Should().BeOfType<long>().And.Be(3_000_000_000L);
    }

    [Fact]
    public void Normalize_JsonNumberDecimal_ReturnsDecimal()
    {
        var element = JsonElementFactory.From(1.5m);
        var result = DataverseValueNormalizer.Normalize(element);
        // 1.5 fits in int? No → it's a decimal. But JSON 1.5 TryGetInt32 fails, TryGetInt64 fails, TryGetDecimal succeeds.
        result.Should().BeOfType<decimal>();
    }

    [Fact]
    public void Normalize_JsonArray_ReturnsNormalizedArray()
    {
        var element = JsonElementFactory.FromArray(1, 2, 3);
        var result = DataverseValueNormalizer.Normalize(element);
        result.Should().BeOfType<object[]>();
        ((object[])result!).Should().HaveCount(3);
    }

    [Fact]
    public void Normalize_JsonObject_ReturnsRawText()
    {
        var element = JsonElementFactory.FromObject(new { name = "test" });
        var result = DataverseValueNormalizer.Normalize(element);
        result.Should().BeOfType<string>();
        ((string)result!).Should().Contain("\"name\"");
    }
}
