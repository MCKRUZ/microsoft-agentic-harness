using System.Text.Json;
using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

public class JsonAlphabetizerHelperTests
{
    [Fact]
    public void AlphabetizeProperties_SortsTopLevelKeys()
    {
        var json = """{"zebra": 1, "apple": 2, "mango": 3}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);

        var doc = JsonDocument.Parse(result);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().ContainInOrder("apple", "mango", "zebra");
    }

    [Fact]
    public void AlphabetizeProperties_SortsNestedObjects()
    {
        var json = """{"outer": {"beta": 1, "alpha": 2}}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);

        var doc = JsonDocument.Parse(result);
        var nested = doc.RootElement.GetProperty("outer");
        var keys = nested.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().ContainInOrder("alpha", "beta");
    }

    [Fact]
    public void AlphabetizeProperties_PreservesArrayOrder()
    {
        var json = """{"items": [3, 1, 2]}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);

        var doc = JsonDocument.Parse(result);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetInt32()).ToList();
        items.Should().ContainInOrder(3, 1, 2);
    }

    [Fact]
    public void AlphabetizeProperties_InvalidJson_Throws()
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties("not json");

        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AlphabetizeProperties_NullOrWhitespace_Throws(string? json)
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties(json!);

        act.Should().Throw<ArgumentException>();
    }
}
