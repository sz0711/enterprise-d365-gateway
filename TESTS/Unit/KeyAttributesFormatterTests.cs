using FluentAssertions;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class KeyAttributesFormatterTests
{
    [Fact]
    public void BuildSignature_SimpleValues_KeepsReadableFormat()
    {
        var signature = KeyAttributesFormatter.BuildSignature(
            "Account",
            new Dictionary<string, object?> { ["AccountNumber"] = "ACCT-1001" });

        signature.Should().Be("account:accountnumber=ACCT-1001");
    }

    [Fact]
    public void BuildSignature_MultipleKeys_OrderedCaseInsensitively()
    {
        var signature = KeyAttributesFormatter.BuildSignature(
            "contact",
            new Dictionary<string, object?> { ["lastname"] = "Doe", ["emailaddress1"] = "a@b.c" });

        signature.Should().Be("contact:emailaddress1=a@b.c,lastname=Doe");
    }

    [Fact]
    public void BuildSignature_ValuesWithSeparators_CannotCollideWithDifferentKeySets()
    {
        // Without escaping these two key sets produce the same signature —
        // corrupting the shared cache and the keyed locks.
        var injected = KeyAttributesFormatter.BuildSignature(
            "account",
            new Dictionary<string, object?> { ["a"] = "1,b=2" });

        var legitimate = KeyAttributesFormatter.BuildSignature(
            "account",
            new Dictionary<string, object?> { ["a"] = "1", ["b"] = "2" });

        injected.Should().NotBe(legitimate);
    }

    [Fact]
    public void BuildSignature_EscapesAllSeparatorCharacters()
    {
        var signature = KeyAttributesFormatter.BuildSignature(
            "account",
            new Dictionary<string, object?> { ["key"] = @"a\b,c=d:e" });

        signature.Should().Be(@"account:key=a\\b\,c\=d\:e");
    }

    [Fact]
    public void BuildSignature_SameInput_IsDeterministic()
    {
        var keys = new Dictionary<string, object?> { ["accountnumber"] = "X-1" };

        var s1 = KeyAttributesFormatter.BuildSignature("account", keys);
        var s2 = KeyAttributesFormatter.BuildSignature("account", keys);

        s1.Should().Be(s2);
    }

    [Fact]
    public void BuildSignature_MissingEntityName_Throws()
    {
        var act = () => KeyAttributesFormatter.BuildSignature(
            " ",
            new Dictionary<string, object?> { ["a"] = "1" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSignature_EmptyKeys_Throws()
    {
        var act = () => KeyAttributesFormatter.BuildSignature(
            "account",
            new Dictionary<string, object?>());

        act.Should().Throw<ArgumentException>();
    }
}
