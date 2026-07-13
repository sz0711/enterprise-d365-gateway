using FluentAssertions;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

/// <summary>
/// Deterministic UTC handling: offset-less ISO strings must be interpreted as
/// UTC on every machine, and non-ISO strings must never be coerced to dates.
/// </summary>
public class DataverseValueNormalizerUtcTests
{
    [Fact]
    public void Normalize_OffsetlessIsoDateTime_InterpretedAsUtc()
    {
        var element = JsonElementFactory.From("2024-06-15T10:30:00");

        var normalized = DataverseValueNormalizer.Normalize(element);

        normalized.Should().BeOfType<DateTime>()
            .Which.Should().Be(new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Normalize_ExplicitOffset_ConvertedToUtc()
    {
        var element = JsonElementFactory.From("2024-06-15T12:30:00+02:00");

        var normalized = DataverseValueNormalizer.Normalize(element);

        normalized.Should().BeOfType<DateTime>()
            .Which.Should().Be(new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("2024-01")]         // incomplete — plausible account-number prefix
    [InlineData("15.06.2024")]      // non-ISO format
    [InlineData("ACC-2024-01-15")]  // ISO-like fragment inside a code
    [InlineData("Jan 15 2024")]     // natural language
    public void Normalize_NonIsoStrings_StayStrings(string value)
    {
        var element = JsonElementFactory.From(value);

        var normalized = DataverseValueNormalizer.Normalize(element);

        normalized.Should().BeOfType<string>().Which.Should().Be(value);
    }

    [Fact]
    public void TryParseUtcDateTime_IsHostTimezoneIndependent()
    {
        DataverseValueNormalizer.TryParseUtcDateTime("2024-06-15T10:30:00", out var result).Should().BeTrue();

        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Hour.Should().Be(10, "offset-less input must be read as UTC, not host-local time");
    }
}
